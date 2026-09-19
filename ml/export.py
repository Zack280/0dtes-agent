import json
from pathlib import Path

import numpy as np
import pandas as pd

ROOT = Path(__file__).resolve().parents[1]
DATA = ROOT / "data"
OUT = Path(__file__).resolve().parent / "features.csv"

FEATURE_COLS = [
    "strategy", "direction", "newsCount", "score", "windowKind",
    "prevDayChangePct", "preMarketChangePct",
    "contract_delta", "contract_entryMid", "delta_abs", "theta_abs",
    "gamma_abs", "vega_abs", "contract_impliedVolatility",
    "moneyness", "spread_width", "daysToExpiry", "hourOfDay", "dayOfWeek",
    "regime",
]

LABEL_COLS = [
    "sent", "optionRet15Pct", "optionRet60Pct", "dirHit15", "dirHit60",
    "return15Pct", "return60Pct", "entrySpot", "price60",
]


def read_jsonl(path: Path) -> pd.DataFrame:
    if not path.exists():
        return pd.DataFrame()
    rows = []
    with open(path, encoding="utf-8") as f:
        for line in f:
            line = line.strip()
            if line:
                rows.append(json.loads(line))
    return pd.DataFrame(rows)


def build() -> pd.DataFrame:
    alerts = read_jsonl(DATA / "alerts.jsonl")
    labels = read_jsonl(DATA / "labels.jsonl")
    df = alerts.merge(labels.drop(columns=[c for c in ["entrySpot", "dirHit15", "dirHit60"] if c in labels.columns], errors="ignore"), on="id", how="inner")

    df["date"] = pd.to_datetime(df["signalTimeUtc"], utc=True).dt.date
    df["signalTimeUtc"] = pd.to_datetime(df["signalTimeUtc"], utc=True)

    contract = pd.json_normalize(df["contract"])
    contract.columns = [f"contract_{c}" for c in contract.columns]
    df = pd.concat([df.reset_index(drop=True), contract.reset_index(drop=True)], axis=1)

    df["delta_abs"] = df["contract_delta"].abs()
    df["theta_abs"] = df["contract_theta"].abs()
    df["gamma_abs"] = df["contract_gamma"].abs()
    df["vega_abs"] = df["contract_vega"].abs()
    df["moneyness"] = (df["contract_strike"] - df["entrySpot"]) / df["entrySpot"].replace(0, np.nan)
    df["spread_width"] = (df["contract_entryAsk"] - df["contract_entryBid"]) / df["contract_entryMid"].replace(0, np.nan)

    keep = ["id", "date", "symbol"] + FEATURE_COLS + LABEL_COLS
    out = df[[c for c in keep if c in df.columns]].copy()
    out = out.dropna(subset=["optionRet15Pct", "optionRet60Pct"])

    out["day_med_ret60"] = out.groupby("date")["optionRet60Pct"].transform("median")
    out["beats_day_med60"] = (out["optionRet60Pct"] > out["day_med_ret60"]).astype(int)

    out = out.sort_values("signalTimeUtc") if "signalTimeUtc" in out else out
    out.to_csv(OUT, index=False)
    return out


if __name__ == "__main__":
    df = build()
    print(f"written {len(df)} rows -> {OUT.name}")
    print(f"days: {df['date'].nunique()}  sent: {df['sent'].sum()}  beats_day_med60: {df['beats_day_med60'].mean():.1%}")
    by_day = df.groupby("date").agg(
        n=("id", "count"), ret60=("optionRet60Pct", "mean"), win60=("optionRet60Pct", lambda s: (s > 0).mean())
    )
    print(by_day.round(1))