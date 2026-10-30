#!/usr/bin/env bash
# open (with and without --as) and users: the guards, the link, the session
# id kept out of logs and JSON, and argv that survives quotes and '+'.
# shellcheck source=tests/lib/harness.sh
. "$(dirname "${BASH_SOURCE[0]}")/lib/harness.sh"

BENCH="$HOME/dev/frappe-bench"
make_fake_bench "$BENCH" v16dev
printf 'port 11000\n' >"$BENCH/config/redis_queue.conf"; printf 'port 13000\n' >"$BENCH/config/redis_cache.conf"
printf '127.0.0.1 v16dev\n' >>"$FL_HOSTS_FILE"
printf '{\n "db_name": "_v16dev",\n "developer_mode": 1\n}\n' >"$BENCH/sites/v16dev/site_config.json"
cat >"$MOCK_STATE/users.json" <<'JSON'
[
 {"name": "Administrator", "full_name": "Administrator", "user_type": "System User", "enabled": 1, "roles": ["System Manager"], "last_login": null},
 {"name": "priya@example.com", "full_name": "Priya Nair", "user_type": "System User", "enabled": 1, "roles": ["NE", "Stock User"], "last_login": "2026-10-29 10:00:00"},
 {"name": "a+b@x.com", "full_name": "O'Brien X", "user_type": "System User", "enabled": 1, "roles": [], "last_login": null},
 {"name": "web@example.com", "full_name": "Web Customer", "user_type": "Website User", "enabled": 1, "roles": ["Customer"], "last_login": null},
 {"name": "gone@example.com", "full_name": "Gone", "user_type": "System User", "enabled": 0, "roles": [], "last_login": null}
]
JSON
bench_up() { add_listener 8000 4242 python; add_listener 13000 4243 redis-server; }

# ---- the bench must run: no session, no python, no browser while it is stopped
run_fm open --as priya@example.com --bench-dir "$BENCH"
assert_eq "1" "$CODE" "$OUT"; assert_contains "$OUT" "The bench is not running."; assert_contains "$OUT" "benchup"
run_fm users --bench-dir "$BENCH"
assert_eq "1" "$CODE" "$OUT"; assert_contains "$OUT" "The bench is not running."
assert_calls_not_contain '^bench-python'
assert_calls_not_contain '^open '
bench_up

# ---- users
run_fm users --json --bench-dir "$BENCH"
assert_eq "0" "$CODE" "$OUT"
assert_eq "v16dev False" "$(printf '%s' "$OUT" | jget - 'd["site"], d["truncated"]' | tr -d "(),'")"
assert_eq "Administrator a+b@x.com priya@example.com web@example.com" "$(printf '%s' "$OUT" | jget - '" ".join(u["name"] for u in d["users"])')" "(enabled only, System Users first)"
assert_eq "1 2" "$(printf '%s' "$OUT" | jget - 'd["schema_version"] > 0 and 1, d["users"][2]["role_count"]' | tr -d "(),'")"
run_fm users --json --search PRI --bench-dir "$BENCH"
assert_eq "priya@example.com" "$(printf '%s' "$OUT" | jget - '" ".join(u["name"] for u in d["users"])')"
run_fm users --json --limit 1 --bench-dir "$BENCH"
assert_eq "True 1" "$(printf '%s' "$OUT" | jget - 'd["truncated"], len(d["users"])' | tr -d "(),'")"
run_fm users --search "o'brien x" --bench-dir "$BENCH"
assert_eq "0" "$CODE" "$OUT"; assert_contains "$OUT" "a+b@x.com"
assert_calls_contain "^bench-python benchbar-users \[v16dev\] \[o'brien x\] \[50\] \[table\]$" "(the search is one argument, quote and space kept)"
run_fm users --search nobody --bench-dir "$BENCH"
assert_eq "0" "$CODE" "$OUT"; assert_contains "$OUT" 'No enabled user on v16dev matches "nobody".'
run_fm users --limit 0 --bench-dir "$BENCH"; assert_eq "1" "$CODE"; assert_contains "$OUT" "--limit needs a number from 1 to 500."
run_fm users --limit abc --bench-dir "$BENCH"; assert_eq "1" "$CODE"; assert_contains "$OUT" "--limit needs a number from 1 to 500."
run_fm users --site nosuch --bench-dir "$BENCH"; assert_eq "1" "$CODE"; assert_contains "$OUT" "No site 'nosuch'"

# ---- open --as: the link, opened once, with the session id only on the opener's line
reset_calls
run_fm open --as priya@example.com --json --bench-dir "$BENCH"
assert_eq "0" "$CODE" "$OUT"
assert_eq "priya@example.com|Priya Nair|System User|/app|True|False" \
  "$(printf '%s' "$OUT" | jget - '"|".join(str(x) for x in (d["user"], d["full_name"], d["user_type"], d["path"], d["opened"], "url" in d))')"
assert_not_contains "$OUT" "mocksid" "(no session id in --json without --print-url)"
assert_calls_contain '^open http://v16dev:8000/login\?redirect-to=%2Fapp&sid=mocksid0123456789&t=[0-9]+$'
assert_eq "1" "$(grep -c '^open ' "$MOCK_LOG")"
assert_calls_contain '^bench-python benchbar-open-as \[v16dev\] \[priya@example.com\]$'
# the session id reaches no file benchbar keeps
if grep -r -q mocksid "$FL_STATE_DIR" 2>/dev/null; then fail "a session id was written under $FL_STATE_DIR"; fi
if grep -r -q mocksid "$BENCH/sites" 2>/dev/null; then fail "a session id was written under sites/"; fi

# --print-url: the link on stdout, nothing opened
reset_calls
run_fm open --as priya@example.com --print-url --json --bench-dir "$BENCH"
assert_eq "0" "$CODE" "$OUT"
assert_eq "False" "$(printf '%s' "$OUT" | jget - 'd["opened"]')"
case "$(printf '%s' "$OUT" | jget - 'd["url"]')" in
  "http://v16dev:8000/login?redirect-to=%2Fapp&sid=mocksid0123456789&t="[0-9]*) ;;
  *) fail "unexpected url: $OUT" ;;
esac
assert_calls_not_contain '^open '
OUT_URL="$("$FM" open --as priya@example.com --print-url --bench-dir "$BENCH" 2>/dev/null)"
assert_eq "1" "$(printf '%s\n' "$OUT_URL" | wc -l | tr -d ' ')" "(--print-url alone prints one line)"
case "$OUT_URL" in http://v16dev:8000/login\?redirect-to=%2Fapp\&sid=mocksid0123456789\&t=[0-9]*) ;; *) fail "unexpected url line: $OUT_URL" ;; esac

# '+' and a quote in the user id stay one argument and come back unchanged
reset_calls
run_fm open --as 'a+b@x.com' --no-browser --json --bench-dir "$BENCH"
assert_eq "0" "$CODE" "$OUT"
assert_eq "a+b@x.com O'Brien X" "$(printf '%s' "$OUT" | jget - 'd["user"] + " " + d["full_name"]')"
assert_calls_contain '^bench-python benchbar-open-as \[v16dev\] \[a\+b@x.com\]$'
assert_calls_not_contain '^open '

# --path: an old sid is dropped, the rest kept and encoded once; a Website User lands on /
reset_calls
run_fm open --as priya@example.com --path '/app/nst?status=Open&sid=old#x' --bench-dir "$BENCH"
assert_eq "0" "$CODE" "$OUT"
assert_calls_contain '^open http://v16dev:8000/login\?redirect-to=%2Fapp%2Fnst%3Fstatus%3DOpen%23x&sid=mocksid0123456789&t=[0-9]+$'
reset_calls
run_fm open --as web@example.com --bench-dir "$BENCH"
assert_calls_contain '^open http://v16dev:8000/login\?redirect-to=%2F&sid='
run_fm open --as priya@example.com --path app/x --bench-dir "$BENCH"; assert_eq "1" "$CODE"; assert_contains "$OUT" "--path must start with a single /."
run_fm open --as priya@example.com --path //evil.example --bench-dir "$BENCH"; assert_eq "1" "$CODE"; assert_contains "$OUT" "--path must start with a single /."

# ---- refusals from the helper come through as they are
run_fm open --as nobody@example.com --bench-dir "$BENCH"; assert_eq "1" "$CODE"; assert_contains "$OUT" "No user 'nobody@example.com' on v16dev."
run_fm open --as gone@example.com --bench-dir "$BENCH"; assert_eq "1" "$CODE"; assert_contains "$OUT" "gone@example.com is disabled on v16dev."
MOCK_OPEN_AS_REFUSE="Frappe refused the login: Access not allowed from this IP Address" run_fm open --as priya@example.com --json --bench-dir "$BENCH"
assert_eq "1" "$CODE"; assert_contains "$OUT" "Frappe refused the login: Access not allowed from this IP Address"
run_fm open --as Guest --bench-dir "$BENCH"; assert_eq "1" "$CODE"; assert_contains "$OUT" "Guest has no session to open."
run_fm open --as --bench-dir "$BENCH"; assert_eq "1" "$CODE"; assert_contains "$OUT" "--as needs a user."
run_fm open --nonsense --bench-dir "$BENCH"; assert_eq "1" "$CODE"; assert_contains "$OUT" "Unknown argument for open: --nonsense"
# a flag that takes a value, given last: a message, not a silent exit
run_fm open --bench-dir "$BENCH" --as priya@example.com --path; assert_eq "1" "$CODE"; assert_contains "$OUT" "--path needs a page."
run_fm users --bench-dir "$BENCH" --search; assert_eq "1" "$CODE"; assert_contains "$OUT" "--search needs some text."
run_fm users --bench-dir "$BENCH" --limit; assert_eq "1" "$CODE"; assert_contains "$OUT" "--limit needs a number from 1 to 500."
# no here-string carries the session id: bash 3.2 writes those to /var/tmp
if grep -n '<<<' "$ROOT/lib/frappe-local/open-as.sh" | grep -q -E 'fields|__out'; then fail "a here-string carries helper output in open-as.sh"; fi

# ---- developer_mode off: refused before any Python runs, Administrator included
printf '{\n "db_name": "_v16dev"\n}\n' >"$BENCH/sites/v16dev/site_config.json"
reset_calls
run_fm open --as Administrator --bench-dir "$BENCH"
assert_eq "1" "$CODE"; assert_contains "$OUT" "v16dev is not in developer mode"; assert_contains "$OUT" "set-config developer_mode 1"
assert_calls_not_contain '^bench-python'
# without --as it is plain Open Site: no gate, no session, no Python
run_fm open --bench-dir "$BENCH"
assert_eq "0" "$CODE" "$OUT"
assert_calls_contain '^open http://v16dev:8000$'
assert_calls_not_contain '^bench-python'
printf '{\n "db_name": "_v16dev",\n "developer_mode": 1\n}\n' >"$BENCH/sites/v16dev/site_config.json"

# ---- a second site: no hosts line is fine for *.localhost, refused otherwise
mkdir -p "$BENCH/sites/two.localhost" "$BENCH/sites/three"
printf '{\n "developer_mode": 1\n}\n' >"$BENCH/sites/two.localhost/site_config.json"
printf '{\n "developer_mode": 1\n}\n' >"$BENCH/sites/three/site_config.json"
reset_calls
run_fm open --site two.localhost --as priya@example.com --bench-dir "$BENCH"
assert_eq "0" "$CODE" "$OUT"
assert_calls_contain '^open http://two.localhost:8000/login\?redirect-to=%2Fapp&sid='
assert_calls_contain '^bench-python benchbar-open-as \[two.localhost\] \[priya@example.com\]$'
run_fm open --site three --as priya@example.com --bench-dir "$BENCH"
assert_eq "1" "$CODE"; assert_contains "$OUT" "three has no hosts line"; assert_contains "$OUT" "benchbar site hosts"

# ---- a second run works the same (a launch has no "unchanged")
reset_calls
run_fm open --as priya@example.com --bench-dir "$BENCH"
assert_eq "0" "$CODE" "$OUT"; assert_contains "$OUT" "v16dev: opened as priya@example.com (Priya Nair, System User) at /app"
run_fm open --as priya@example.com --bench-dir "$BENCH"
assert_eq "0" "$CODE" "$OUT"; assert_eq "2" "$(grep -c '^open ' "$MOCK_LOG")"

# ---- no browser opener: refused with the way out
MOCK_OPEN_EXIT=127 run_fm open --as priya@example.com --bench-dir "$BENCH"
assert_eq "1" "$CODE"; assert_contains "$OUT" "benchbar open --print-url"

# ---- Linux opens with xdg-open, WSL with wslview (the Windows browser); a
# Linux bench's sites are *.localhost names, which resolve without a hosts line
use_linux
reset_calls
run_fm open --site two.localhost --as priya@example.com --bench-dir "$BENCH"
assert_eq "0" "$CODE" "$OUT"
assert_calls_contain '^wslview http://two.localhost:8000/login\?redirect-to=%2Fapp&sid=mocksid0123456789&t=[0-9]+$'
printf 'Linux version 6.8.0-45-generic (buildd@lcy02) #45-Ubuntu SMP\n' >"$FL_PROC_VERSION"
reset_calls
run_fm open --site two.localhost --as priya@example.com --bench-dir "$BENCH"
assert_eq "0" "$CODE" "$OUT"
assert_calls_contain '^xdg-open http://two.localhost:8000/login\?redirect-to=%2Fapp&sid='

echo "PASS test-open-as"
