use crate::profile::Region;
use futures_util::{future::AbortHandle, future::Abortable};
use reqwest::Url;
use serde::{Deserialize, Serialize};
use serde_json::{json, Value};
use std::{
    collections::HashMap,
    path::PathBuf,
    process::Stdio,
    sync::{Arc, Mutex},
    time::Duration,
};
use tauri::{ipc::Channel, AppHandle, Manager, State, WebviewWindow};
use tokio::{
    io::{AsyncBufReadExt, AsyncWriteExt, BufReader},
    process::{ChildStdin, Command as ProcessCommand},
    sync::{mpsc, oneshot},
    time::Instant,
};

const MAX_MESSAGE_BYTES: usize = 8 * 1024 * 1024;
const CONNECT_TIMEOUT: Duration = Duration::from_secs(10);
const CLOSE_TIMEOUT: Duration = Duration::from_secs(5);

#[derive(Clone, Default)]
pub struct SocketState {
    connections: Arc<Mutex<HashMap<(String, String), Connection>>>,
}

struct Connection {
    commands: mpsc::Sender<Command>,
    abort: AbortHandle,
    events: Channel<SocketEvent>,
    generation: Arc<()>,
}

enum Command {
    Send(SocketData, oneshot::Sender<Result<(), String>>),
    Close(Option<u16>, String),
}

#[derive(Clone, Serialize)]
#[serde(tag = "type", rename_all = "snake_case")]
pub enum SocketEvent {
    Open { protocol: String, extensions: String },
    Text { data: String },
    Binary { data: Vec<u8> },
    Error { message: String },
    Close {
        code: u16,
        reason: String,
        #[serde(rename = "wasClean")]
        was_clean: bool,
    },
}

#[derive(Deserialize, Serialize)]
#[serde(tag = "type", content = "data", rename_all = "lowercase")]
pub enum SocketData {
    Text(String),
    Binary(Vec<u8>),
}

fn validate_destination(region: Region, value: &str) -> Result<Url, String> {
    let url = Url::parse(value).map_err(|_| "Invalid WebSocket URL")?;
    if url.scheme() != "wss"
        || url.host_str() != Some(region.api_host())
        || url.port_or_known_default() != Some(443)
        || url.path() != "/socket.io/"
        || !url.username().is_empty()
        || url.password().is_some()
        || url.fragment().is_some()
    {
        return Err("Only the current region's official WebSocket endpoint is allowed".into());
    }
    Ok(url)
}

fn validate_caller(label: &str, url: &Url) -> Result<(), String> {
    let local =
        (url.scheme() == "tauri" && url.host_str() == Some("localhost") && url.port().is_none())
            || (matches!(url.scheme(), "http" | "https")
                && url.host_str() == Some("tauri.localhost")
                && url.port().is_none());
    if !matches!(label, "main" | "panel" | "login") || !local {
        return Err("WebSocket access requires a trusted app window".into());
    }
    Ok(())
}

fn caller_key(window: &WebviewWindow, id: &str) -> Result<(String, String), String> {
    validate_caller(
        window.label(),
        &window.url().map_err(|_| "Window location is unavailable")?,
    )?;
    if id.is_empty() || id.len() > 96 || !id.bytes().all(|b| b.is_ascii_alphanumeric() || b == b'-') {
        return Err("Invalid WebSocket id".into());
    }
    Ok((window.label().into(), id.into()))
}

fn protocol_token(value: &str) -> bool {
    !value.is_empty()
        && value.bytes().all(|b| b.is_ascii_alphanumeric() || b"!#$%&'*+-.^_\u0060|~".contains(&b))
}

fn validate_protocols(protocols: &[String]) -> Result<(), String> {
    for (index, value) in protocols.iter().enumerate() {
        if !protocol_token(value) || protocols[..index].contains(value) {
            return Err("Invalid WebSocket subprotocol".into());
        }
    }
    Ok(())
}

fn failed(events: &Channel<SocketEvent>, message: &str) {
    let _ = events.send(SocketEvent::Error { message: message.into() });
    let _ = events.send(SocketEvent::Close {
        code: 1006,
        reason: String::new(),
        was_clean: false,
    });
}

async fn send_json(stdin: &mut ChildStdin, value: Value) -> Result<(), String> {
    let mut bytes = serde_json::to_vec(&value).map_err(|_| "WebSocket message encode failed")?;
    bytes.push(b'\n');
    stdin.write_all(&bytes).await.map_err(|_| "WebSocket helper closed")?;
    stdin.flush().await.map_err(|_| "WebSocket helper closed")
}

fn decode_event(value: &Value) -> Option<SocketEvent> {
    match value.get("type")?.as_str()? {
        "open" => Some(SocketEvent::Open {
            protocol: value.get("protocol").and_then(Value::as_str).unwrap_or("").into(),
            extensions: value.get("extensions").and_then(Value::as_str).unwrap_or("").into(),
        }),
        "text" => Some(SocketEvent::Text { data: value.get("data")?.as_str()?.into() }),
        "binary" => {
            let list = value.get("data")?.as_array()?;
            if list.len() > MAX_MESSAGE_BYTES { return None; }
            let bytes = list.iter().map(|v| u8::try_from(v.as_u64()?).ok()).collect::<Option<Vec<_>>>()?;
            Some(SocketEvent::Binary { data: bytes })
        }
        "error" => {
            let raw = value.get("message")?.as_str()?;
            let permitted = matches!(
                raw,
                "WebSocket TLS connection failed"
                    | "WebSocket network connection failed"
                    | "WebSocket protocol connection failed"
            ) || raw.strip_prefix("WebSocket handshake rejected (HTTP ")
                .and_then(|v| v.strip_suffix(')'))
                .is_some_and(|v| v.len() == 3 && v.bytes().all(|b| b.is_ascii_digit()));
            Some(SocketEvent::Error {
                message: if permitted { raw.into() } else { "WebSocket native connection failed".into() },
            })
        }
        "close" => Some(SocketEvent::Close {
            code: u16::try_from(value.get("code")?.as_u64()?).ok()?,
            reason: value.get("reason").and_then(Value::as_str).unwrap_or("").into(),
            was_clean: value.get("wasClean").and_then(Value::as_bool).unwrap_or(false),
        }),
        _ => None,
    }
}

async fn run_connection(
    helper: PathBuf,
    url: Url,
    protocols: Vec<String>,
    events: Channel<SocketEvent>,
    mut commands: mpsc::Receiver<Command>,
) {
    let mut process = match ProcessCommand::new(helper)
        .stdin(Stdio::piped())
        .stdout(Stdio::piped())
        .stderr(Stdio::null())
        .kill_on_drop(true)
        .spawn()
    {
        Ok(process) => process,
        Err(_) => {
            failed(&events, "WebSocket native bridge unavailable");
            return;
        }
    };
    let Some(mut stdin) = process.stdin.take() else {
        failed(&events, "WebSocket native bridge unavailable");
        return;
    };
    let Some(stdout) = process.stdout.take() else {
        failed(&events, "WebSocket native bridge unavailable");
        return;
    };
    if send_json(&mut stdin, json!({"action":"open","url":url.as_str(),"protocols":protocols})).await.is_err() {
        failed(&events, "WebSocket native bridge unavailable");
        return;
    }

    let mut lines = BufReader::new(stdout).lines();
    let mut opened = false;
    let connect_deadline = Instant::now() + CONNECT_TIMEOUT;
    let mut close_deadline: Option<Instant> = None;
    let mut sequence: u64 = 0;
    let mut pending: HashMap<u64, oneshot::Sender<Result<(), String>>> = HashMap::new();

    loop {
        tokio::select! {
            _ = tokio::time::sleep_until(connect_deadline), if !opened => {
                failed(&events, "WebSocket connection timed out");
                return;
            }
            _ = tokio::time::sleep_until(close_deadline.unwrap_or_else(|| Instant::now() + Duration::from_secs(86400))), if close_deadline.is_some() => {
                failed(&events, "WebSocket close timed out");
                return;
            }
            command = commands.recv() => match command {
                Some(Command::Send(data, reply)) if opened && close_deadline.is_none() => {
                    sequence += 1;
                    if send_json(&mut stdin, json!({"action":"send","sequence":sequence,"data":data})).await.is_err() {
                        let _ = reply.send(Err("WebSocket helper closed".into()));
                        failed(&events, "WebSocket native bridge unavailable");
                        return;
                    }
                    pending.insert(sequence, reply);
                }
                Some(Command::Send(_, reply)) => {
                    let _ = reply.send(Err("WebSocket is not open".into()));
                }
                Some(Command::Close(code, reason)) if close_deadline.is_none() => {
                    close_deadline = Some(Instant::now() + CLOSE_TIMEOUT);
                    if send_json(&mut stdin, json!({"action":"close","code":code.unwrap_or(1000),"reason":reason})).await.is_err() {
                        failed(&events, "WebSocket close failed");
                        return;
                    }
                }
                Some(Command::Close(..)) => {}
                None => return,
            },
            next = lines.next_line() => {
                let line = match next {
                    Ok(Some(line)) if line.len() < MAX_MESSAGE_BYTES * 4 => line,
                    _ => {
                        failed(&events, "WebSocket native bridge unavailable");
                        return;
                    }
                };
                let Ok(message) = serde_json::from_str::<Value>(&line) else {
                    failed(&events, "WebSocket native protocol failed");
                    return;
                };
                let Some(kind) = message.get("type").and_then(Value::as_str) else { continue; };
                if kind == "send_ok" || kind == "send_error" {
                    if let Some(id) = message.get("sequence").and_then(Value::as_u64) {
                        if let Some(reply) = pending.remove(&id) {
                            let _ = reply.send(if kind == "send_ok" { Ok(()) } else { Err("WebSocket send failed".into()) });
                        }
                    }
                    continue;
                }
                let Some(event) = decode_event(&message) else {
                    failed(&events, "WebSocket native protocol failed");
                    return;
                };
                if matches!(event, SocketEvent::Open{..}) { opened = true; }
                let terminal = matches!(event, SocketEvent::Close{..});
                if events.send(event).is_err() || terminal { return; }
            }
        }
    }
}

#[tauri::command]
pub fn native_ws_connect(
    window: WebviewWindow,
    state: State<'_, SocketState>,
    id: String,
    url: String,
    protocols: Vec<String>,
    events: Channel<SocketEvent>,
) -> Result<(), String> {
    let key = caller_key(&window, &id)?;
    let region = Region::from_app(window.app_handle());
    let destination = validate_destination(region, &url)?;
    validate_protocols(&protocols)?;
    let helper = window.app_handle().path().resource_dir().map_err(|_| "WebSocket helper path unavailable")?
        .join("native-ws-helper");
    let (sender, receiver) = mpsc::channel(32);
    let (abort, registration) = AbortHandle::new_pair();
    let generation = Arc::new(());
    {
        let mut connections = state.connections.lock().map_err(|_| "WebSocket state is unavailable")?;
        if window.app_handle().try_state::<crate::fullatlas::FullAtlasState>()
            .is_some_and(|state| state.enabled())
        {
            return Err("本地全图鉴开启时不能连接联机房间".into());
        }
        if connections.contains_key(&key) { return Err("WebSocket id already exists".into()); }
        if connections.len() >= 16 { return Err("Too many WebSocket connections".into()); }
        connections.insert(key.clone(), Connection {
            commands: sender, abort, events: events.clone(), generation: generation.clone()
        });
    }
    let connections = state.connections.clone();
    tauri::async_runtime::spawn(async move {
        let _ = Abortable::new(run_connection(helper, destination, protocols, events, receiver), registration).await;
        if let Ok(mut connections) = connections.lock() {
            if connections.get(&key).is_some_and(|connection| Arc::ptr_eq(&connection.generation, &generation)) {
                connections.remove(&key);
            }
        }
    });
    Ok(())
}

fn sender(window: &WebviewWindow, state: &SocketState, id: &str) -> Result<mpsc::Sender<Command>, String> {
    let key = caller_key(window, id)?;
    state.connections.lock().map_err(|_| "WebSocket state is unavailable")?
        .get(&key).map(|connection| connection.commands.clone())
        .ok_or_else(|| "WebSocket is closed".into())
}

#[tauri::command]
pub async fn native_ws_send(
    window: WebviewWindow,
    state: State<'_, SocketState>,
    id: String,
    data: SocketData,
) -> Result<(), String> {
    let sender = sender(&window, &state, &id)?;
    match &data {
        SocketData::Text(value) if value.len() <= MAX_MESSAGE_BYTES => {}
        SocketData::Binary(value) if value.len() <= MAX_MESSAGE_BYTES => {}
        _ => return Err("WebSocket message is too large".into()),
    }
    let (reply, result) = oneshot::channel();
    sender.send(Command::Send(data, reply)).await.map_err(|_| "WebSocket is closed")?;
    result.await.map_err(|_| "WebSocket is closed")?
}

fn validate_close(code: Option<u16>, reason: &str) -> Result<(), String> {
    if reason.len() > 123 || code.is_some_and(|code| code != 1000 && !(3000..=4999).contains(&code)) {
        return Err("Invalid WebSocket close code or reason".into());
    }
    Ok(())
}

#[tauri::command]
pub async fn native_ws_close(
    window: WebviewWindow,
    state: State<'_, SocketState>,
    id: String,
    code: Option<u16>,
    reason: Option<String>,
) -> Result<(), String> {
    let sender = sender(&window, &state, &id)?;
    let reason = reason.unwrap_or_default();
    validate_close(code, &reason)?;
    sender.send(Command::Close(code, reason)).await.map_err(|_| "WebSocket is closed".into())
}

fn remove_connections(state: &SocketState, label: Option<&str>) {
    if let Ok(mut connections) = state.connections.lock() {
        connections.retain(|(owner, _), connection| {
            if label.is_none_or(|label| owner == label) {
                connection.abort.abort();
                let _ = connection.events.send(SocketEvent::Close {
                    code: 1001,
                    reason: String::new(),
                    was_clean: false,
                });
                false
            } else {
                true
            }
        });
    }
}

pub fn close_window(app: &AppHandle, label: &str) {
    if let Some(state) = app.try_state::<SocketState>() {
        remove_connections(&state, Some(label));
    }
}

pub fn shutdown(app: &AppHandle) {
    if let Some(state) = app.try_state::<SocketState>() {
        remove_connections(&state, None);
    }
}

#[cfg(test)]
mod tests {
    use super::*;

    #[test]
    fn validates_exact_region_endpoint() {
        let host = Region::Cn.api_host();
        assert!(validate_destination(
            Region::Cn,
            &format!("wss://{host}/socket.io/?EIO=3&transport=websocket&token=synthetic")
        ).is_ok());
        for url in [
            format!("ws://{host}/socket.io/"),
            format!("wss://{host}:444/socket.io/"),
            format!("wss://{host}/other"),
            format!("wss://{host}.example/socket.io/"),
            format!("wss://user@{host}/socket.io/"),
        ] {
            assert!(validate_destination(Region::Cn, &url).is_err());
        }
        assert!(validate_destination(
            Region::Cn,
            &format!("wss://{}/socket.io/", Region::Global.api_host())
        ).is_err());
    }

    #[test]
    fn only_local_windows_can_create_sockets() {
        assert!(validate_caller("main", &Url::parse("tauri://localhost/index.html").unwrap()).is_ok());
        assert!(validate_caller("panel", &Url::parse("http://tauri.localhost/index.html").unwrap()).is_ok());
        assert!(validate_caller("main", &Url::parse("https://example.com").unwrap()).is_err());
    }

    #[test]
    fn checks_subprotocols_and_close_codes() {
        assert!(validate_protocols(&["sample.v1".into()]).is_ok());
        assert!(validate_protocols(&["same".into(), "same".into()]).is_err());
        assert!(validate_protocols(&["a,b".into()]).is_err());
        assert!(validate_close(Some(1000), "").is_ok());
        assert!(validate_close(Some(3001), "done").is_ok());
        assert!(validate_close(Some(1006), "").is_err());
        assert!(validate_close(Some(1000), &"x".repeat(124)).is_err());
    }

    #[test]
    fn accepts_only_safe_helper_messages() {
        assert!(matches!(decode_event(&json!({"type":"open","protocol":"","extensions":""})), Some(SocketEvent::Open{..})));
        assert!(matches!(decode_event(&json!({"type":"text","data":"42[\"rj\"]"})), Some(SocketEvent::Text{..})));
        assert!(matches!(decode_event(&json!({"type":"binary","data":[0,1,255]})), Some(SocketEvent::Binary{..})));
        assert!(matches!(decode_event(&json!({"type":"error","message":"WebSocket TLS connection failed"})), Some(SocketEvent::Error{..})));
        assert!(matches!(decode_event(&json!({"type":"close","code":1006,"wasClean":false})), Some(SocketEvent::Close{..})));
    }

    #[test]
    fn window_cleanup_aborts_only_its_connections() {
        let state = SocketState::default();
        let mut handles = Vec::new();
        for label in ["main", "panel"] {
            let (sender, _receiver) = mpsc::channel(1);
            let (abort, _registration) = AbortHandle::new_pair();
            handles.push(abort.clone());
            state.connections.lock().unwrap().insert(
                (label.into(), "synthetic".into()),
                Connection {
                    commands: sender,
                    abort,
                    events: Channel::new(|_| Ok(())),
                    generation: Arc::new(()),
                },
            );
        }
        remove_connections(&state, Some("panel"));
        assert_eq!(state.connections.lock().unwrap().len(), 1);
        assert!(!handles[0].is_aborted());
        assert!(handles[1].is_aborted());
        remove_connections(&state, None);
        assert!(state.connections.lock().unwrap().is_empty());
        assert!(handles[0].is_aborted());
    }
}
