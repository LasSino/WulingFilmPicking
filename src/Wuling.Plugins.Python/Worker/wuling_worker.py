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
        if method == "action":
            return self._action(params, msg_id)
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
                plugin_dir = manifest.get("plugin_dir") or manifest.get("pluginDir")
                if plugin_dir and plugin_dir not in sys.path:
                    sys.path.insert(0, plugin_dir)
                module_name, _, class_name = entry.partition(":")
                module = __import__(module_name)
                cls = getattr(module, class_name)
                instance = cls()
                if not isinstance(instance, PluginBase):
                    raise TypeError(f"{class_name} is not a PluginBase subclass")
                instance._bind_manifest(manifest)
                self._plugins[pid] = instance
                ok.append(pid)
            except Exception as exc:  # 崩溃隔离: 单插件失败不影响 Worker
                failed[pid] = f"{type(exc).__name__}: {exc}"
        return self._ok(msg_id, {"ok": ok, "failed": failed, "api": API_VERSION})

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
    # 通知宿主 Worker 就绪
    print(json.dumps({"jsonrpc": "2.0", "method": "ready", "params": {"api": API_VERSION}}), flush=True)
    for line in sys.stdin:
        line = line.strip()
        if not line:
            continue
        try:
            msg = json.loads(line)
        except json.JSONDecodeError as exc:
            print(json.dumps({"jsonrpc": "2.0", "id": None, "error": {"code": -32700, "message": f"parse error: {exc}"}}), flush=True)
            continue
        try:
            resp = worker.handle(msg)
        except Exception as exc:  # 兜底: 任何未捕获异常都转为 error 响应
            resp = {"jsonrpc": "2.0", "id": msg.get("id"), "error": {"code": -32603, "message": f"internal error: {exc}"}}
        print(json.dumps(resp, ensure_ascii=False), flush=True)
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
