"""Local-only, manually gated Responses API for real WinUI smoke verification.

python tests/gui-smoke-server.py --root artifacts/gui-smoke
Set MADI_HOME to the printed storage directory before launching the published EXE.
POST /release/<prompt> to finish a stream; disconnects record cancellation.
No credentials or external API calls are used.
"""
import argparse
import datetime
import json
import pathlib
import threading
import urllib.parse
from http.server import BaseHTTPRequestHandler, ThreadingHTTPServer

parser = argparse.ArgumentParser()
parser.add_argument("--root", required=True)
args = parser.parse_args()
root = pathlib.Path(args.root).resolve()
root.mkdir(parents=True, exist_ok=True)
workspace = root / "project"
storage = root / "storage"
workspace.mkdir(exist_ok=True)
storage.mkdir(exist_ok=True)
signals = {}
gate = threading.Lock()

def record(event, prompt):
    with gate:
        with (root / "requests.jsonl").open("a", encoding="utf-8") as log:
            log.write(json.dumps({"event": event, "prompt": prompt, "time": datetime.datetime.now(datetime.timezone.utc).isoformat()}) + "\n")

class Handler(BaseHTTPRequestHandler):
    protocol_version = "HTTP/1.1"
    def log_message(self, *unused):
        pass
    def do_POST(self):
        body = self.rfile.read(int(self.headers.get("Content-Length", "0")))
        if self.path.startswith("/release/"):
            label = urllib.parse.unquote(self.path[len("/release/"):])
            with gate:
                signals.setdefault(label, threading.Event()).set()
            self.send_response(200); self.send_header("Content-Length", "2"); self.end_headers(); self.wfile.write(b"ok")
            return
        request = json.loads(body)
        user = [item for item in request["input"] if item.get("role") == "user"][-1]
        prompt = "".join(c.get("text", "") for c in user["content"])
        with gate:
            signal = signals.setdefault(prompt, threading.Event())
        record("started", prompt)
        self.send_response(200)
        self.send_header("Content-Type", "text/event-stream")
        self.send_header("Connection", "close")
        self.end_headers()
        def send(event):
            self.wfile.write(("data: " + json.dumps(event, ensure_ascii=False) + "\n\n").encode("utf-8")); self.wfile.flush()
        try:
            send({"type": "response.output_text.delta", "delta": f"{prompt}: 병렬 실행 중입니다.\n\n"})
            while not signal.wait(0.2):
                self.wfile.write(b": waiting\n\n"); self.wfile.flush()
            text = f"{prompt}: 완료했습니다."
            send({"type": "response.output_text.delta", "delta": text})
            send({"type": "response.completed", "response": {"id": "smoke", "output": [{"type": "message", "role": "assistant", "content": [{"type": "output_text", "text": text}]}], "usage": {"input_tokens": 10, "output_tokens": 8}}})
            record("completed", prompt)
        except (BrokenPipeError, ConnectionResetError, ConnectionAbortedError):
            record("canceled", prompt)
        self.close_connection = True

server = ThreadingHTTPServer(("127.0.0.1", 0), Handler)
port = server.server_address[1]
(root / "port.txt").write_text(str(port))
def write(path, value):
    path.parent.mkdir(parents=True, exist_ok=True)
    path.write_text(json.dumps(value, ensure_ascii=False), encoding="utf-8")
write(storage / "models.json", [{"provider": "LocalSmoke", "api": "openai-responses", "baseUrl": f"http://127.0.0.1:{port}/v1", "apiKey": "sk-local-smoke-only", "models": [{"name": "deterministic", "maxContextTokens": 100000, "supportsReasoning": True}]}])
write(storage / "ui" / "gui-settings.json", {"RecentWorkspaces": [str(workspace)]})
write(storage / "works" / "W.json", {"schemaVersion": 2, "id": "W", "title": "공유 Work", "context": "공통 지침", "workingDirectory": str(workspace)})
for sid, title, wid in [("A", "세션 A", "W"), ("C", "세션 C", None)]:
    write(storage / "sessions" / sid / "session.json", {"schemaVersion": 2, "id": sid, "title": title, "workId": wid, "workingDirectory": str(workspace), "model": {"model": "deterministic", "provider": "LocalSmoke", "reasoningEffort": "medium"}})
    lines = []
    if sid == "A":
        for i in range(20):
            lines.extend([{"sequence": i * 2, "type": "user_message", "text": f"이전 요청 {i + 1}"}, {"sequence": i * 2 + 1, "type": "assistant_message", "text": f"이전 답변 {i + 1}\n\n스크롤 복원 확인용 대화입니다."}])
    (storage / "sessions" / sid / "conversation.jsonl").write_text("".join(json.dumps(line, ensure_ascii=False) + "\n" for line in lines), encoding="utf-8")
write(storage / "current.json", {"sessionId": "A"})
print(json.dumps({"port": port, "storage": str(storage), "workspace": str(workspace)}), flush=True)
server.serve_forever()
