#!/usr/bin/env python3
"""武陵选片局 Python Worker — 常驻子进程 (设计 04 §4)。

JSON-RPC 2.0 over stdio, 每行一个消息 (NDJSON)。二进制不进 stdio。

协议方法:
  initialize  {plugins: [manifest...]}        -> {result: {ok: [id...], failed: {id: err}}}
  render      {mode, slots: [{display_name, preview_path, has_raw, tag}]}  -> {view: <视图树>}
  shutdown    {}                              -> 进程退出

插件侧契约 (wuling_sdk.PluginBase):
  def render(self, slot) -> dict   # 声明式视图树 (kind: image/text/table/kv/histogram/curve/stack/row/grid)
  def on_action(self, action, payload) -> dict | None

崩溃隔离: 任何插件异常都转为 JSON-RPC error, Worker 不退出;
宿主侧超时 3s -> 占位, 累计 3 次 -> 插件禁用。
"""
from __future__ import annotations

import json
import os
import sys
import traceback
from typing import Any

# Windows 默认 stdout 为 GBK; 宿主按 UTF-8 读取 NDJSON → 中文必乱码, 强制 UTF-8
if hasattr(sys.stdout, "reconfigure"):
    sys.stdout.reconfigure(encoding="utf-8")
    sys.stderr.reconfigure(encoding="utf-8")
os.environ.setdefault("PYTHONIOENCODING", "utf-8")

# wuling_sdk 与 worker 同目录分发 (随应用 plugins/python/)
sys.path.insert(0, str(__import__("pathlib").Path(__file__).parent))

from wuling_sdk import PluginBase  # noqa: E402

API_VERSION = 1


def _make_slot(params: dict[str, Any]) -> Any:
    """把宿主传来的槽位 dict 包装成 SDK 的 SlotView 对象。"""
    from wuling_sdk import SlotView

    return SlotView(
        display_name=params.get("display_name", ""),
        preview_path=params.get("preview_path"),
        raw_path=params.get("raw_path"),
        jpeg_path=params.get("jpeg_path"),
        has_raw=params.get("has_raw", False),
        tag=params.get("tag", "unmarked"),
        index=params.get("index", 0),
        datahints=params.get("datahints") or {},
    )


class Worker:
    def __init__(self) -> None:
        self._plugins: dict[str, PluginBase] = {}

    def handle(self, msg: dict[str, Any]) -> dict[str, Any]:
        method = msg.get("method", "")
        params = msg.get("params") or {}
        msg_id = msg.get("id")

        if method == "initialize":
            return self._initialize(params, msg_id)
        if method == "render":
            return self._render(params, msg_id)
        if method == "render_slot":
            return self._render_slot(params, msg_id)
        if method == "action":
            return self._action(params, msg_id)
        if method == "service.histogram":
            return self._service(params, msg_id, "histogram")
        if method == "service.exif":
            return self._service(params, msg_id, "exif")
        if method == "service.file_meta":
            return self._service(params, msg_id, "file_meta")
        if method == "shutdown":
            return self._ok(msg_id, {"bye": True})

        return self._error(msg_id, -32601, f"unknown method: {method}")

    # ---- initialize ----
    def _initialize(self, params: dict[str, Any], msg_id: Any) -> dict[str, Any]:
        ok: list[str] = []
        failed: dict[str, str] = {}
        for manifest in params.get("plugins", []):
            pid = manifest.get("id", "?")
            if manifest.get("api") != API_VERSION:
                failed[pid] = f"api version mismatch: {manifest.get('api')}"
                continue
            entry = manifest.get("entry", "")
            try:
                instance = self._load_plugin(manifest, entry)
                if not isinstance(instance, PluginBase):
                    raise TypeError(f"not a PluginBase subclass")
                instance._bind_manifest(manifest)
                self._plugins[pid] = instance
                ok.append(pid)
            except Exception as exc:  # 崩溃隔离: 单插件失败不影响 Worker
                failed[pid] = f"{type(exc).__name__}: {exc}"
        return self._ok(msg_id, {"ok": ok, "failed": failed, "api": API_VERSION})

    def _load_plugin(self, manifest: dict[str, Any], entry: str) -> Any:
        """按 manifest 隔离装载插件(04 §4): importlib 直载入口文件, 独立模块名。

        不能用 sys.path + __import__: 多个插件的入口都叫 main.py, sys.modules 缓存
        会让第二个插件 import 到第一个插件的模块(实例串台)。
        """
        import importlib.util
        import pathlib

        plugin_dir = manifest.get("plugin_dir") or manifest.get("pluginDir")
        if not plugin_dir:
            raise ValueError("manifest missing plugin_dir")

        module_name, _, class_name = entry.partition(":")
        entry_file = pathlib.Path(plugin_dir) / f"{module_name}.py"
        if not entry_file.exists():
            raise FileNotFoundError(f"entry not found: {entry_file}")

        # 唯一模块名: plugin id 去非法字符(如 "py.histogram" → "wuling_plugin_py_histogram")
        unique = "wuling_plugin_" + manifest.get("id", "anon").replace("-", "_").replace(".", "_").replace(":", "_")
        spec = importlib.util.spec_from_file_location(unique, entry_file)
        if spec is None or spec.loader is None:
            raise ImportError(f"cannot load spec for {entry_file}")
        module = importlib.util.module_from_spec(spec)
        sys.modules[unique] = module  # dataclass/相对导入需要
        # 注意: 插件目录不进 sys.path —— wuling_sdk 钉死为 Worker 同目录的唯一副本,
        # 否则每个插件目录各 import 一次 SDK 会产生多个 PluginBase 类, isinstance 跨副本失败
        spec.loader.exec_module(module)
        cls = getattr(module, class_name)
        return cls()  # 实例化(此前漏了, 返回的是类对象本身)

    # ---- render ----
    def _render(self, params: dict[str, Any], msg_id: Any) -> dict[str, Any]:
        pid = params.get("mode", "")
        plugin = self._plugins.get(pid)
        if plugin is None:
            return self._error(msg_id, -32000, f"plugin not loaded: {pid}")

        slots = [_make_slot(s) for s in params.get("slots", [])]
        try:
            views = [plugin.render(slot) for slot in slots]
        except Exception as exc:
            return self._error(msg_id, -32001, f"render failed: {type(exc).__name__}: {exc}\n{traceback.format_exc(limit=3)}")
        return self._ok(msg_id, {"view": {"kind": "row", "children": views}})

    # ---- render_slot (04 §4.6.4 按槽异步的前置;先到先回) ----
    def _render_slot(self, params: dict[str, Any], msg_id: Any) -> dict[str, Any]:
        pid = params.get("mode", "")
        plugin = self._plugins.get(pid)
        if plugin is None:
            return self._error(msg_id, -32000, f"plugin not loaded: {pid}")

        try:
            slot = _make_slot(params.get("slot") or {})
            view = plugin.render(slot)
        except Exception as exc:
            return self._error(msg_id, -32001, f"render failed: {type(exc).__name__}: {exc}\n{traceback.format_exc(limit=3)}")
        return self._ok(msg_id, {"view": view})

    # ---- services (宿主数据服务转发: exif/histogram/fileMeta;文件路径引用交换, 04 §3.1) ----
    def _service(self, params: dict[str, Any], msg_id: Any, name: str) -> dict[str, Any]:
        pid = params.get("mode", "")
        plugin = self._plugins.get(pid)
        if plugin is None:
            return self._error(msg_id, -32000, f"plugin not loaded: {pid}")
        try:
            args = params.get("args") or []
            fn = getattr(plugin, "_services", {}).get(name)
            if fn is None:
                return self._error(msg_id, -32003, f"service not available: {name}")
            return self._ok(msg_id, {"result": fn(*args)})
        except Exception as exc:
            return self._error(msg_id, -32002, f"service {name} failed: {type(exc).__name__}: {exc}")

    # ---- action ----
    def _action(self, params: dict[str, Any], msg_id: Any) -> dict[str, Any]:
        pid = params.get("mode", "")
        plugin = self._plugins.get(pid)
        if plugin is None:
            return self._error(msg_id, -32000, f"plugin not loaded: {pid}")
        try:
            result = plugin.on_action(params.get("action", ""), params.get("payload"))
        except Exception as exc:
            return self._error(msg_id, -32002, f"action failed: {type(exc).__name__}: {exc}")
        return self._ok(msg_id, {"result": result})

    @staticmethod
    def _ok(msg_id: Any, result: dict[str, Any]) -> dict[str, Any]:
        return {"jsonrpc": "2.0", "id": msg_id, "result": result}

    @staticmethod
    def _error(msg_id: Any, code: int, message: str) -> dict[str, Any]:
        return {"jsonrpc": "2.0", "id": msg_id, "error": {"code": code, "message": message}}


def main() -> int:
    worker = Worker()

    def send(obj: dict[str, Any]) -> None:
        print(json.dumps(obj, ensure_ascii=False), flush=True)

    # 插件数据服务(04 §3.1): 同步函数, 内部向宿主发 JSON-RPC 请求并等待响应。
    # 宿主回包带同 id; 主循环按 id 分发。
    import threading

    _pending_host: dict[int, dict[str, Any] | None] = {}
    _next_host_id = [0]
    _io_lock = threading.Lock()

    def call_host(method: str, params: dict[str, Any], timeout: float = 10.0) -> dict[str, Any]:
        with _io_lock:
            _next_host_id[0] += 1
            req_id = _next_host_id[0]
            send({"jsonrpc": "2.0", "id": req_id, "method": method, "params": params})
        # 主线程从 stdin 读到该 id 的响应后填入 _pending_host
        import time
        deadline = time.time() + timeout
        while time.time() < deadline:
            if req_id in _pending_host:
                resp = _pending_host.pop(req_id)
                if resp is None:
                    raise RuntimeError(f"host service {method} failed")
                if "error" in resp:
                    raise RuntimeError(resp["error"].get("message", "host error"))
                return resp.get("result") or {}
            time.sleep(0.005)
        raise TimeoutError(f"host service {method} timed out")

    def _svc_histogram(path: str) -> dict[str, Any]:
        r = call_host("host.histogram", {"path": path})
        return {"r": r.get("r", []), "g": r.get("g", []), "b": r.get("b", [])}

    def _svc_exif(path: str) -> dict[str, Any]:
        return call_host("host.exif", {"path": path})

    def _svc_file_meta(path: str) -> dict[str, Any]:
        return call_host("host.fileMeta", {"path": path})

    services = {"histogram": _svc_histogram, "exif": _svc_exif, "file_meta": _svc_file_meta}

    def handle_msg(msg: dict[str, Any]) -> None:
        # 响应(宿主回包): 分发给等待中的服务调用
        if msg.get("method") is None and "id" in msg and isinstance(msg["id"], int):
            _pending_host[msg["id"]] = msg
            return
        try:
            resp = worker.handle(msg)
        except Exception as exc:  # 兜底: 任何未捕获异常都转为 error 响应
            resp = {"jsonrpc": "2.0", "id": msg.get("id"), "error": {"code": -32603, "message": f"internal error: {exc}"}}
        send(resp)

    # 通知宿主 Worker 就绪
    send({"jsonrpc": "2.0", "method": "ready", "params": {"api": API_VERSION}})
    for line in sys.stdin:
        line = line.strip()
        if not line:
            continue
        try:
            msg = json.loads(line)
        except json.JSONDecodeError as exc:
            send({"jsonrpc": "2.0", "id": None, "error": {"code": -32700, "message": f"parse error: {exc}"}})
            continue
        handle_msg(msg)
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
