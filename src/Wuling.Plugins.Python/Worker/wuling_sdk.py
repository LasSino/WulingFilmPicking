"""wuling_sdk — 武陵选片局 Python 插件 SDK (纯标准库, 设计 04 §4)。

随应用分发, 与 wuling_worker.py 同目录。插件 import 本模块实现对比模式。
"""
from __future__ import annotations

import json
import os
from typing import Any, Callable


class SlotView:
    """单个槽位的只读视图 — 宿主传入, 插件只读。"""

    def __init__(
        self,
        display_name: str,
        preview_path: str | None,
        raw_path: str | None,
        jpeg_path: str | None,
        has_raw: bool,
        tag: str,
        index: int,
        datahints: dict[str, Any] | None = None,
    ) -> None:
        self.display_name = display_name
        self.preview_path = preview_path
        self.raw_path = raw_path
        self.jpeg_path = jpeg_path
        self.has_raw = has_raw
        self.tag = tag
        self.index = index
        #: 宿主随请求内联的小数值数据(04 §3 通道③): histogram = {"r":[...], "g":[...], "b":[...]}
        self.datahints: dict[str, Any] = datahints or {}

    def __repr__(self) -> str:  # pragma: no cover
        return f"<SlotView {self.display_name} tag={self.tag}>"


class PluginBase:
    """对比模式插件基类。子类实现 render(slot) 返回声明式视图树。"""

    #: 子类可覆盖: 槽位数量范围 (默认全范围)
    min_slots = 1
    max_slots = 9

    def __init__(self) -> None:
        self._manifest: dict[str, Any] = {}
        self._services: dict[str, Callable[..., Any]] = {}

    # ---- Worker 内部绑定 ----
    def _bind_manifest(self, manifest: dict[str, Any]) -> None:
        self._manifest = manifest

    def _bind_services(self, services: dict[str, Callable[..., Any]]) -> None:
        """宿主经 Worker 注入的数据服务代理 (exif/histogram/pixels)。

        每个服务是同步函数, 内部经 JSON-RPC 请求宿主。首版由 Worker 桥接。
        """
        self._services = services

    # ---- 插件 API ----
    @property
    def plugin_id(self) -> str:
        return self._manifest.get("id", self.__class__.__name__)

    def exif(self, slot: SlotView) -> dict[str, str]:
        """EXIF 摘要 (路径引用交换, 不取像素)。宿主缓存, 首调可能阻塞。"""
        fn = self._services.get("exif")
        if fn is None:
            return {}
        return fn(slot.preview_path or "")

    def histogram(self, slot: SlotView) -> dict[str, list[int]]:
        """直方图 (小数组直接传: 256 bin x 3 通道)。"""
        fn = self._services.get("histogram")
        if fn is None:
            return {"r": [], "g": [], "b": []}
        return fn(slot.preview_path or "")

    # ---- 必须实现 ----
    def render(self, slot: SlotView) -> dict[str, Any]:
        """返回该槽位的声明式视图树。

        视图元素 kind: image / text / table / kv / histogram / curve / stack / row / grid。
        image.src 用路径引用 (通道①), 不含像素本体。
        """
        raise NotImplementedError

    def on_action(self, action: str, payload: Any) -> Any:  # noqa: ARG002
        """宿主回调 (刷新/参数变化); 默认无操作。"""
        return None


# ---- 声明式视图构造器 (便捷方法; 也可直接手写 dict) ----

def image(src: str | None, fit: str = "contain") -> dict[str, Any]:
    return {"kind": "image", "src": src, "fit": fit}


def text(value: str, size: int = 12, bold: bool = False, opacity: float = 1.0) -> dict[str, Any]:
    return {"kind": "text", "value": value, "size": size, "bold": bold, "opacity": opacity}


def kv(pairs: list[tuple[str, Any]]) -> dict[str, Any]:
    return {"kind": "kv", "pairs": [[k, str(v)] for k, v in pairs]}


def table(rows: list[list[Any]], header: list[str] | None = None) -> dict[str, Any]:
    return {"kind": "table", "rows": [[str(c) for c in r] for r in rows], "header": header}


def histogram(data: dict[str, list[int]]) -> dict[str, Any]:
    return {"kind": "histogram", "r": data.get("r", []), "g": data.get("g", []), "b": data.get("b", [])}


def curve(points: list[tuple[float, float]]) -> dict[str, Any]:
    return {"kind": "curve", "points": [[float(x), float(y)] for x, y in points]}


def stack(*children: dict[str, Any], spacing: int = 4) -> dict[str, Any]:
    return {"kind": "stack", "children": list(children), "spacing": spacing}


def row(*children: dict[str, Any], spacing: int = 4) -> dict[str, Any]:
    return {"kind": "row", "children": list(children), "spacing": spacing}


def grid(columns: int, *children: dict[str, Any]) -> dict[str, Any]:
    return {"kind": "grid", "columns": columns, "children": list(children)}
