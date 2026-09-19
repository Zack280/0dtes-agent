import warnings
from pathlib import Path

import numpy as np
import pandas as pd
from sklearn.ensemble import HistGradientBoostingClassifier
from sklearn.linear_model import LogisticRegression
from sklearn.metrics import roc_auc_score
from sklearn.pipeline import make_pipeline
from sklearn.preprocessing import OneHotEncoder, StandardScaler

warnings.filterwarnings("ignore")

ROOT = Path(__file__).resolve().parents[1]
FEATURES = Path(__file__).resolve().parent / "features.csv"

CAT_COLS = ["symbol", "strategy", "windowKind", "regime"]
NUM_COLS = [
    "direction", "newsCount", "score", "prevDayChangePct", "preMarketChangePct",
    "contract_delta", "contract_entryMid", "delta_abs", "theta_abs", "gamma_abs",
    "vega_abs", "contract_impliedVolatility", "moneyness", "spread_width",
    "daysToExpiry", "hourOfDay", "dayOfWeek",
]


def load() -> pd.DataFrame:
    df = pd.read_csv(FEATURES)
    df["date"] = pd.to_datetime(df["date"])
    return df


def feature_matrix(df: pd.DataFrame, train_idx: np.ndarray, test_idx: np.ndarray):
    train, test = df.iloc[train_idx], df.iloc[test_idx]
    for c in CAT_COLS + NUM_COLS:
        if c in df and df[c].dtype == object:
            df[c] = df[c].astype("category")
    cat = [c for c in CAT_COLS if c in df.columns]
    num = [c for c in NUM_COLS if c in df.columns]
    enc = OneHotEncoder(handle_unknown="ignore")
    enc.fit(train[cat])
    Xtr = np.hstack([enc.transform(train[cat]).toarray(), train[num].to_numpy(float)])
    Xte = np.hstack([enc.transform(test[cat]).toarray(), test[num].to_numpy(float)])
    return Xtr, Xte


def select_top(df: pd.DataFrame, idx: np.ndarray, score, k: int):
    s = np.asarray(score)[idx]
    order = np.argsort(-s, kind="stable")[:k]
    return df.iloc[np.asarray(idx)[order]]


def summarize(picked: pd.DataFrame, name: str):
    if len(picked) == 0:
        return name, 0, np.nan, np.nan, 0.0, 0.0
    ret = picked["optionRet60Pct"]
    return name, len(picked), ret.mean(), (ret > 0).mean(), ret.median(), picked["beats_day_med60"].mean()


def main():
    df = load()
    days = np.sort(df["date"].unique())
    results = {k: [] for k in ["all", "gate", "logreg", "gbm"]}
    aucs = []

    for i, test_date in enumerate(days):
        if i < 2:
            continue
        train_idx = np.where(df["date"] < test_date)[0]
        test_idx = np.where(df["date"] == test_date)[0]
        if len(train_idx) < 20 or len(test_idx) < 5:
            continue

        day = df.iloc[test_idx].copy()
        k = int(day["sent"].sum())
        if k == 0:
            k = max(1, len(day) // 3)

        Xtr, Xte = feature_matrix(df, train_idx, test_idx)
        ytr = df.iloc[train_idx]["beats_day_med60"].to_numpy()

        logreg = make_pipeline(StandardScaler(), LogisticRegression(max_iter=2000))
        gbm = HistGradientBoostingClassifier(max_iter=200, learning_rate=0.05, max_depth=3)
        models = {"logreg": logreg, "gbm": gbm}
        preds = {}
        for name, model in models.items():
            model.fit(Xtr, ytr)
            p = model.predict_proba(Xte)[:, 1]
            preds[name] = p
            if len(np.unique(ytr)) > 1 and len(np.unique(day["beats_day_med60"].to_numpy())) > 1:
                try:
                    aucs.append(roc_auc_score(day["beats_day_med60"].to_numpy(), p))
                except ValueError:
                    pass

        results["all"].append(summarize(day, "send-all"))
        results["gate"].append(summarize(day[day["sent"]], "gate(sent)"))
        results["logreg"].append(summarize(select_top(day, np.arange(len(day)), preds["logreg"], k), "logreg-topk"))
        results["gbm"].append(summarize(select_top(day, np.arange(len(day)), preds["gbm"], k), "gbm-topk"))

    print(f"{'selector':<14}{'n':>5}{'ret60':>9}{'win60':>8}{'med60':>9}{'beatmed':>9}")
    for name, entries in results.items():
        flat = [t for t in entries if t and len(t) == 6 and t[1] > 0]
        if not flat:
            continue
        label = flat[0][0]
        n = sum(t[1] for t in flat)
        mean = np.average([t[2] for t in flat], weights=[t[1] for t in flat])
        win = np.average([t[3] for t in flat], weights=[t[1] for t in flat])
        med = np.average([t[4] for t in flat], weights=[t[1] for t in flat])
        bm = np.average([t[5] for t in flat], weights=[t[1] for t in flat])
        print(f"{label:<14}{n:>5}{mean:>9.2f}{win:>8.1%}{med:>9.2f}{bm:>9.1%}")

    if aucs:
        print(f"\navg AUROC (beat day-median): {np.mean(aucs):.3f}")

    trend = Path(__file__).resolve().parent / "results.csv"
    summary = {}
    for name, entries in results.items():
        flat = [t for t in entries if t and len(t) == 6 and t[1] > 0]
        if flat:
            summary[name] = np.average([t[2] for t in flat], weights=[t[1] for t in flat])
    row = {
        "runUtc": pd.Timestamp.utcnow().isoformat(),
        "rows": len(df),
        "days": len(days),
        "auroc60": round(float(np.mean(aucs)), 3) if aucs else "",
        "ret60_sendall": round(summary.get("all", np.nan), 2),
        "ret60_gate": round(summary.get("gate", np.nan), 2),
        "ret60_logreg": round(summary.get("logreg", np.nan), 2),
        "ret60_gbm": round(summary.get("gbm", np.nan), 2),
    }
    header = list(row.keys())
    new = pd.DataFrame([row])[header]
    if trend.exists():
        prior = pd.read_csv(trend)
        prior = prior[[c for c in prior.columns if c in header]]
        oversized = prior[prior["rows"].astype(float) > int(np.ceil(len(df) * 1.1))]
        prior = prior[~prior.index.isin(oversized.index)]
        out = pd.concat([prior, new], ignore_index=True)
        out.to_csv(trend, index=False)
    else:
        new.to_csv(trend, index=False)
    print(f"\nappended trend row -> {trend.name} ({len(out) if 'out' in dir() else 1} runs)")


if __name__ == "__main__":
    main()