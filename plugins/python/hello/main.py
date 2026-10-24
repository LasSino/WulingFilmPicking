"""Hello 对比模式 (验收用, 设计 04 §6): 每槽显示文件名与预览图引用。"""
from __future__ import annotations

from typing import Any

from wuling_sdk import PluginBase, SlotView, image, kv, stack, text


class WulingPlugin(PluginBase):
    def render(self, slot: SlotView) -> dict[str, Any]:
        return stack(
            image(slot.preview_path, fit="contain"),
            text(slot.display_name, size=13, bold=True),
            kv([
                ("来源", "RAW+JPG" if slot.has_raw else "JPG"),
                ("标记", slot.tag),
            ]),
        )

    def on_action(self, action: str, payload: Any) -> None:
        return None
