import Foundation
import Darwin

private final class NativeSocket: NSObject, URLSessionWebSocketDelegate, URLSessionTaskDelegate {
    private var task: URLSessionWebSocketTask?
    private var finished = false
    private var opened = false
    private lazy var session: URLSession = {
        let config = URLSessionConfiguration.ephemeral
        config.timeoutIntervalForRequest = 10
        return URLSession(configuration: config, delegate: self, delegateQueue: .main)
    }()

    private func emit(_ value: [String: Any]) {
        guard let bytes = try? JSONSerialization.data(withJSONObject: value) else { return }
        FileHandle.standardOutput.write(bytes)
        FileHandle.standardOutput.write(Data([10]))
    }

    private func fail(_ error: Error?, task: URLSessionTask?) {
        if finished { return }
        finished = true
        let status = (task?.response as? HTTPURLResponse)?.statusCode ?? 0
        let code = (error as NSError?)?.code ?? 0
        let message: String
        if status != 0 && status != 101 {
            message = "WebSocket handshake rejected (HTTP \(status))"
        } else if [-1200, -1201, -1202, -1203, -1204, -1205, -1206].contains(code) {
            message = "WebSocket TLS connection failed"
        } else {
            message = "WebSocket network connection failed"
        }
        emit(["type": "error", "message": message])
        emit(["type": "close", "code": 1006, "reason": "", "wasClean": false])
        self.task?.cancel()
        self.task = nil
    }

    func handle(_ request: [String: Any]) {
        guard let action = request["action"] as? String else { return }
        switch action {
        case "open":
            guard task == nil, !finished,
                  let address = request["url"] as? String,
                  let url = URL(string: address),
                  let parts = URLComponents(url: url, resolvingAgainstBaseURL: false),
                  url.scheme == "wss", url.user == nil, url.password == nil,
                  url.fragment == nil, url.port == nil || url.port == 443,
                  ["api-mc-wuwatappo.kurogames-global.com",
                   "api-wutheringwaves-wuwatappo.kurogames-global.com"].contains(url.host ?? ""),
                  parts.path == "/socket.io/",
                  parts.queryItems?.contains(where: { $0.name == "token" && !($0.value ?? "").isEmpty }) == true
            else {
                fail(nil, task: nil)
                return
            }
            let protocols = request["protocols"] as? [String] ?? []
            var urlRequest = URLRequest(url: url)
            urlRequest.timeoutInterval = 10
            let socket = protocols.isEmpty
                ? session.webSocketTask(with: urlRequest)
                : session.webSocketTask(with: url, protocols: protocols)
            task = socket
            socket.resume()
        case "send":
            guard let sequence = request["sequence"] as? Int else { return }
            guard let task = task, opened, !finished,
                  let data = request["data"] as? [String: Any],
                  let kind = data["type"] as? String else {
                emit(["type": "send_error", "sequence": sequence])
                return
            }
            let message: URLSessionWebSocketTask.Message
            if kind == "text", let text = data["data"] as? String,
               text.utf8.count <= 8 * 1024 * 1024 {
                message = .string(text)
            } else if kind == "binary", let array = data["data"] as? [Int],
                      array.count <= 8 * 1024 * 1024,
                      array.allSatisfy({ $0 >= 0 && $0 <= 255 }) {
                message = .data(Data(array.map(UInt8.init)))
            } else {
                emit(["type": "send_error", "sequence": sequence])
                return
            }
            task.send(message) { [weak self] error in
                DispatchQueue.main.async {
                    guard let self = self, !self.finished else { return }
                    self.emit(["type": error == nil ? "send_ok" : "send_error",
                               "sequence": sequence])
                    if let error = error { self.fail(error, task: task) }
                }
            }
        case "close":
            guard let task = task, !finished else { return }
            let code = request["code"] as? Int ?? 1000
            let reason = (request["reason"] as? String ?? "").data(using: .utf8)
            task.cancel(with: URLSessionWebSocketTask.CloseCode(rawValue: code) ?? .normalClosure,
                        reason: reason)
        default:
            break
        }
    }

    private func receive(_ task: URLSessionWebSocketTask) {
        guard !finished else { return }
        task.receive { [weak self, weak task] result in
            DispatchQueue.main.async {
                guard let self = self, let task = task, !self.finished else { return }
                switch result {
                case .success(.string(let text)):
                    if text.utf8.count > 8 * 1024 * 1024 {
                        self.fail(nil, task: task)
                    } else {
                        self.emit(["type": "text", "data": text])
                        self.receive(task)
                    }
                case .success(.data(let data)):
                    if data.count > 8 * 1024 * 1024 {
                        self.fail(nil, task: task)
                    } else {
                        self.emit(["type": "binary", "data": Array(data)])
                        self.receive(task)
                    }
                case .failure(let error):
                    self.fail(error, task: task)
                @unknown default:
                    self.fail(nil, task: task)
                }
            }
        }
    }

    func urlSession(_ session: URLSession, webSocketTask: URLSessionWebSocketTask,
                    didOpenWithProtocol protocol: String?) {
        guard !finished else { return }
        opened = true
        emit(["type": "open", "protocol": `protocol` ?? "", "extensions": ""])
        receive(webSocketTask)
    }

    func urlSession(_ session: URLSession, webSocketTask: URLSessionWebSocketTask,
                    didCloseWith closeCode: URLSessionWebSocketTask.CloseCode, reason: Data?) {
        guard !finished else { return }
        finished = true
        emit(["type": "close", "code": closeCode.rawValue,
              "reason": reason.flatMap { String(data: $0, encoding: .utf8) } ?? "",
              "wasClean": true])
        task = nil
    }

    func urlSession(_ session: URLSession, task: URLSessionTask,
                    didCompleteWithError error: Error?) {
        if let error = error { fail(error, task: task) }
    }

    func stop() {
        self.task?.cancel(with: .goingAway, reason: nil)
        session.invalidateAndCancel()
    }
}

private let bridge = NativeSocket()
DispatchQueue.global(qos: .userInitiated).async {
    while let line = readLine() {
        guard let bytes = line.data(using: .utf8),
              let value = try? JSONSerialization.jsonObject(with: bytes) as? [String: Any]
        else { continue }
        DispatchQueue.main.async { bridge.handle(value) }
    }
    DispatchQueue.main.async {
        bridge.stop()
        exit(0)
    }
}
dispatchMain()
