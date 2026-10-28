"""色彩直方图模式 (TODO #6, 03 F6 色彩曲线预览的直方图部分)。

数据来源: 宿主 datahints 内联直方图(04 §3 通道③, 256bin×3 ≈ 3KB, 纯 stdlib 无需 numpy)。
每槽渲染: 直方图(声明式) + 亮度/对比度摘要。
"""
from __future__ import annotations

from typing import Any

from wuling_sdk import PluginBase, SlotView, histogram, kv, stack, text


def _percentile(hist: list[int], total: int, pct: float) -> int:
    """累计到 pct 分位的 bin 下标(黑场/白场估计)。"""
    acc = 0
    target = total * pct
    for i, v in enumerate(hist):
        acc += v
        if acc >= target:
            return i
    return 255


class WulingPlugin(PluginBase):
    def render(self, slot: SlotView) -> dict[str, Any]:
        hist = (slot.datahints or {}).get("histogram") or {}
        r, g, b = hist.get("r") or [], hist.get("g") or [], hist.get("b") or []

        if not r:
            return stack(
                text(slot.display_name, size=13, bold=True),
                text("(直方图数据不可用)", size=11, opacity=0.6),
            )

        # 亮度/对比度 Δ 指标(03 F6 差分模式的摘要口径): 均值 ± 标准差
        total = sum(r) + sum(g) + sum(b)
        n = max(total // 3, 1)
        mean_r = sum(i * v for i, v in enumerate(r)) / max(sum(r), 1)
        mean_g = sum(i * v for i, v in enumerate(g)) / max(sum(g), 1)
        mean_b = sum(i * v for i, v in enumerate(b)) / max(sum(b), 1)
        luma = 0.299 * mean_r + 0.587 * mean_g + 0.114 * mean_b
        blacks = _percentile([rr + gg + bb for rr, gg, bb in zip(r, g, b)], total, 0.005)
        whites = _percentile([rr + gg + bb for rr, gg, bb in zip(r, g, b)], total, 0.995)
        contrast = whites - blacks

        return stack(
            text(slot.display_name, size=13, bold=True),
            histogram({"r": r, "g": g, "b": b}),
            kv([
                ("亮度", f"{luma:.0f}/255"),
                ("动态范围", f"{blacks}-{whites} ({contrast} 级)"),
                ("R/G/B 均值", f"{mean_r:.0f}/{mean_g:.0f}/{mean_b:.0f}"),
            ]),
        )

    def on_action(self, action: str, payload: Any) -> None:
        return None
