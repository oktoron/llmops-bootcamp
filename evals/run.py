import argparse
import json
import os
import sys
import urllib.request

BASE = os.environ.get("SERVICE_URL", "http://localhost:8080")

# Windows-консоль за замовчуванням cp1252: кирилиця у відповідях моделі вбивала
# прогін UnicodeEncodeError-ом ще до вердикту. Вивід — завжди UTF-8.
if hasattr(sys.stdout, "reconfigure"):
    sys.stdout.reconfigure(encoding="utf-8", errors="replace")

# Внутрішній сервіс — повз будь-який проксі (інакше localhost іде через корп-проксі → 404).
_opener = urllib.request.build_opener(urllib.request.ProxyHandler({}))


def call(message):
    data = json.dumps({"message": message}).encode("utf-8")
    req = urllib.request.Request(
        BASE + "/chat", data=data, headers={"Content-Type": "application/json"}
    )
    with _opener.open(req, timeout=30) as r:
        # усе тіло, а не лише content: --expect-all-fail читає ще й blocked
        return json.loads(r.read())


def grade(case, answer):
    a = answer.lower()
    if case.get("expect_refusal"):
        # «вибачте» прибрано зі списку: воно є і в заглушці graceful degradation,
        # і refusal-кейс проходив би навіть під час повного outage
        return any(w in a for w in ["не можу", "cannot", "refuse"])
    for kw in case.get("expect", []):
        if kw.lower() not in a:
            return False
    for kw in case.get("forbid", []):
        if kw.lower() in a:
            return False
    # TODO(student): model-based grader (LLM-as-judge) — потребує реального ключа.
    return True


def run(path, threshold, prefix="", expect_all_fail=False):
    cases = [json.loads(x) for x in open(path, encoding="utf-8") if x.strip()]
    passed = 0
    green = []    # (кейс, blocked) для кейсів, що пройшли
    errors = []   # id кейсів без оцінки: виняток, не-2xx, недоступний сервіс
    for c in cases:
        try:
            # префікс (напр. «__fail_503 ») дописуємо на льоту — копія датасету не потрібна
            body = call(prefix + c["input"])
        except Exception as e:  # noqa: BLE001
            print(f"  {c['id']}: ERROR {e}")
            errors.append(c["id"])
            continue
        ans = body.get("content") or ""
        ok = grade(c, ans)
        passed += int(ok)
        if ok:
            green.append((c, body.get("blocked") is True))
        print(f"  {c['id']}: {'ok' if ok else 'FAIL'} — {ans[:60]!r}")

    if expect_all_fail:
        # перевірка хибних спрацювань: під зламаним провайдером жоден кейс не мусить пройти.
        # виняток — лише expect_refusal, на який відмовив сам guardrail (blocked: true у відповіді /chat):
        # він відповідає ще до моделі, тож відмова чесна. відмова від моделі чи заглушки — фальш-позитив
        guard = [c["id"] for c, blocked in green if c.get("expect_refusal") and blocked]
        false_pos = [c["id"] for c, blocked in green if not (c.get("expect_refusal") and blocked)]
        print(f"eval (expect-all-fail, prefix {prefix!r}): {passed}/{len(cases)} passed")
        print(f"  expect_refusal passed via guardrail (blocked: true — allowed): {', '.join(guard) or '—'}")
        failed = False
        if false_pos:
            print(f"  FALSE POSITIVE: {', '.join(false_pos)}")
            failed = True
        # кейс з помилкою не оцінено: мертвий стек або 500/503 на шляху деградації інакше дали б «зелений» прогін
        if errors:
            print(f"  ERROR: {len(errors)} case(s) not graded — check is void: {', '.join(errors)}")
            failed = True
        return 1 if failed else 0

    print(f"eval: {passed}/{len(cases)} passed, threshold {threshold}")
    return 0 if passed >= threshold else 1


if __name__ == "__main__":
    p = argparse.ArgumentParser()
    p.add_argument("--dataset", required=True)
    p.add_argument("--threshold", type=int)
    p.add_argument("--input-prefix", default="",
                   help="рядок, який дописується перед input кожного кейсу (напр. \"__fail_503 \")")
    p.add_argument("--expect-all-fail", action="store_true",
                   help="exit 1, якщо пройшов будь-який кейс, крім expect_refusal з blocked: true (guardrail), "
                        "або якщо хоч один кейс завершився помилкою; з --threshold несумісний")
    args = p.parse_args()
    if args.threshold is None and not args.expect_all_fail:
        p.error("потрібен --threshold (без нього можна лише з --expect-all-fail)")
    # разом не мають сенсу: --expect-all-fail поріг не читає. мовчки ігнорувати не можна — той, хто передав
    # --threshold, думав би, що поріг перевірено. p.error друкує повідомлення і виходить з кодом 2
    if args.threshold is not None and args.expect_all_fail:
        p.error("--threshold не можна поєднувати з --expect-all-fail (цей режим поріг не читає)")
    sys.exit(run(args.dataset, args.threshold, args.input_prefix, args.expect_all_fail))
