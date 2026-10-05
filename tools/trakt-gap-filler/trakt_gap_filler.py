#!/usr/bin/env python3
"""Add the episode plays the Jellyfin Trakt plugin fails to send to Trakt.

Why this exists: on 2026-10-05 an anime episode played to the end in Jellyfin never reached Trakt.
Jellyfin's library numbers it S03E02 (TVDB order) and holds its TVDB and IMDb ids. Trakt (TMDB order)
lists it as S01E26 and, one day after it aired, held only its TMDB id. The plugin's scrobble by ids and
its fallback by show plus S03E02 both got HTTP 404, and the plugin logs a 404 only at Debug level, so
nothing showed.

Each run, for every Jellyfin user linked in the Trakt plugin with scrobbling or "set watched" on:
- lists the episodes Jellyfin marks played whose last play falls inside the lookback (never before
  TRAKT_GAP_FILLER_SINCE);
- finds the episode on Trakt: by the episode's own ids (the owning show must match), otherwise in the
  show's Trakt episode list by air date (within a day), preferring the same season and number, then the
  same title. Anything that does not resolve to exactly one episode is left alone and reported once;
- skips it if Trakt already holds any play of that episode (cached confirmations are kept per Trakt account and
  dropped when the plugin is linked to a different one). Jellyfin marks an episode played only when
  it is played to the end or marked by hand, so an episode Trakt has never seen is a missed play; a
  replay of an episode Trakt already has is not filled;
- adds one play, dated by Jellyfin's playback-stopped activity entry (else the last-played time), once
  the gap has been seen on two runs at least the settle time apart, so it never races the plugin.
Without --apply it only logs what it would do. It never removes anything from Trakt and never refreshes
the plugin's login.
"""

from __future__ import annotations

import argparse
import fcntl
import gzip
import html
import json
import os
import re
import sys
import time
import unicodedata
import urllib.error
import urllib.parse
import urllib.request
import xml.etree.ElementTree as ET
from collections.abc import Callable
from dataclasses import dataclass, field
from datetime import datetime, timezone
from pathlib import Path
from typing import Any, Protocol

NAME = "trakt-gap-filler"
STATE_DIR = os.environ.get("STATE_DIRECTORY", f"/var/lib/{NAME}")
TRAKT_URL = "https://api.trakt.tv"
USER_AGENT = f"{NAME}/1.0"
ID_KEYS = (("Tvdb", "tvdb"), ("Tmdb", "tmdb"), ("Imdb", "imdb"))
HEX64 = re.compile(rb"(?:[0-9a-f]\x00){64}")
TIME_RE = re.compile(r"^(\d{4}-\d\d-\d\dT\d\d:\d\d:\d\d)(?:\.(\d+))?(?:Z|\+00:00)?$")

Clock = Callable[[], float]


def log(level: str, message: str) -> None:
    now = datetime.now(timezone.utc).strftime("%Y-%m-%dT%H:%M:%SZ")
    print(f"[{now}] [{level}] [{NAME}] {message}", file=sys.stderr, flush=True)


class ApiError(RuntimeError):
    pass


class Unresolved(Exception):
    pass


class Api(Protocol):
    def call(self, method: str, path: str, query: dict[str, str] | None = None, body: Any = None) -> Any: ...


class Http:
    """JSON over HTTP with gzip; retries only 429, 5xx and network errors, at most 3 attempts."""

    def __init__(
        self, base: str, headers: dict[str, str], spacing: float, sleep: Callable[[float], None] = time.sleep
    ) -> None:
        self.base = base.rstrip("/")
        self.headers = headers
        self.spacing = spacing
        self.sleep = sleep

    def call(self, method: str, path: str, query: dict[str, str] | None = None, body: Any = None) -> Any:
        url = self.base + path + ("?" + urllib.parse.urlencode(query) if query else "")
        data = None if body is None else json.dumps(body).encode()
        headers = {**self.headers, "User-Agent": USER_AGENT, "Accept": "application/json", "Accept-Encoding": "gzip"}
        if data is not None:
            headers["Content-Type"] = "application/json"
        last = ""
        for attempt in range(1, 4):
            self.sleep(self.spacing)
            request = urllib.request.Request(url, data=data, headers=headers, method=method)
            try:
                with urllib.request.urlopen(request, timeout=60) as response:
                    status, reply, raw = response.status, response.headers, response.read()
            except urllib.error.HTTPError as error:
                status, reply, raw = error.code, error.headers, error.read()
            except (urllib.error.URLError, TimeoutError, ConnectionError) as error:
                last = f"network error: {error}"
                self.sleep(2.0 * attempt)
                continue
            if reply.get("Content-Encoding") == "gzip":
                raw = gzip.decompress(raw)
            text = raw.decode("utf-8", "replace").strip()
            if status == 429 or status >= 500:
                last = f"HTTP {status}: {text[:200]}"
                self.sleep(min(float(reply.get("Retry-After") or 2.0 * attempt), 60.0))
                continue
            if text.startswith("<"):
                raise ApiError(f"{method} {path}: HTML instead of JSON (HTTP {status})")
            if status >= 400:
                raise ApiError(f"{method} {path}: HTTP {status}: {text[:200]}")
            return json.loads(text) if text else None
        raise ApiError(f"{method} {path}: gave up after 3 attempts: {last}")


def send_telegram(text: str) -> None:
    token = os.environ.get("TELEGRAM_BOT_TOKEN") or os.environ.get("UPLOAD_TG_BOT_TOKEN", "")
    chat = os.environ.get("TELEGRAM_CHAT_ID") or os.environ.get("UPLOAD_TG_CHAT_ID", "")
    if not token or not chat:
        log("WARN", "Telegram not configured; the event is only in the journal")
        return
    body = urllib.parse.urlencode(
        {"chat_id": chat, "text": text, "parse_mode": "HTML", "disable_web_page_preview": "true"}
    ).encode()
    try:
        with urllib.request.urlopen(f"https://api.telegram.org/bot{token}/sendMessage", data=body, timeout=20) as reply:
            if json.loads(reply.read()).get("ok") is not True:
                log("WARN", "Telegram answered without ok=true")
    except (urllib.error.URLError, TimeoutError, json.JSONDecodeError) as error:
        log("WARN", f"Telegram notification failed: {error}")


def parse_time(value: str | None) -> datetime | None:
    """Jellyfin writes 7 fractional digits; keep 6 and read everything as UTC."""
    match = TIME_RE.match(value or "")
    if not match:
        return None
    fraction = (match.group(2) or "0")[:6].ljust(6, "0")
    return datetime.fromisoformat(f"{match.group(1)}.{fraction}+00:00")


def trakt_time(moment: datetime) -> str:
    return moment.astimezone(timezone.utc).strftime("%Y-%m-%dT%H:%M:%S.000Z")


def norm_title(text: str) -> str:
    return re.sub(r"[^0-9a-z]+", "", unicodedata.normalize("NFKD", text).casefold())


@dataclass
class TraktUser:
    jellyfin_id: str  # 32 hex digits, no dashes, as Jellyfin's API prints ids
    token: str
    excluded: list[str] = field(default_factory=list)


def read_trakt_users(config: Path) -> list[TraktUser]:
    users = []
    for node in ET.parse(config).getroot().iter("TraktUser"):
        token = node.findtext("AccessToken") or ""
        posts = (node.findtext("Scrobble") or "").lower() == "true" or (
            node.findtext("PostSetWatched") or ""
        ).lower() == "true"
        if token and posts:
            excluded = [e.text.rstrip("/") for e in node.iterfind("LocationsExcluded/string") if e.text]
            users.append(TraktUser((node.findtext("LinkedMbUserId") or "").replace("-", "").lower(), token, excluded))
    return users


def plugin_client_ids(plugins: Path) -> list[str]:
    """The 64-hex strings in the newest Trakt plugin DLL: its client id and its secret, in no known order."""
    dlls = sorted(plugins.glob("Trakt_*/Trakt.dll"), key=lambda p: [int(x) for x in re.findall(r"\d+", p.parent.name)])
    if not dlls:
        raise ApiError(f"no Trakt_*/Trakt.dll under {plugins}")
    return sorted({m.group(0).decode("utf-16-le") for m in HEX64.finditer(dlls[-1].read_bytes())})


def pick_client_id(candidates: list[str], probe: Callable[[str], bool]) -> str:
    """The token works only with the client id it was issued to, so exactly one candidate must answer."""
    working = [c for c in candidates if probe(c)]
    if len(working) != 1:
        raise ApiError(f"{len(working)} of {len(candidates)} client id candidates are accepted by Trakt")
    return working[0]


@dataclass
class Resolver:
    trakt: Api
    shows: dict[tuple[tuple[str, str], ...], int] = field(default_factory=dict)
    seasons: dict[int, list[dict[str, Any]]] = field(default_factory=dict)

    def show(self, series_ids: dict[str, str]) -> int:
        key = tuple(sorted((k, v) for k, v in series_ids.items() if k in dict(ID_KEYS) and v))
        if key not in self.shows:
            found = set()
            for jf_key, source in ID_KEYS:
                if series_ids.get(jf_key):
                    for hit in (
                        self.trakt.call(
                            "GET", f"/search/{source}/{urllib.parse.quote(series_ids[jf_key])}", {"type": "show"}
                        )
                        or []
                    ):
                        found.add(hit["show"]["ids"]["trakt"])
            if len(found) != 1:
                raise Unresolved(f"the series ids {dict(key)} match {len(found)} Trakt shows")
            self.shows[key] = found.pop()
        return self.shows[key]

    def episode(self, show: int, item: dict[str, Any]) -> tuple[dict[str, Any], str]:
        ids = item.get("ProviderIds") or {}
        by_id = {}
        for jf_key, source in ID_KEYS:
            if ids.get(jf_key):
                for hit in (
                    self.trakt.call("GET", f"/search/{source}/{urllib.parse.quote(ids[jf_key])}", {"type": "episode"})
                    or []
                ):
                    if hit.get("show", {}).get("ids", {}).get("trakt") == show:
                        by_id[hit["episode"]["ids"]["trakt"]] = hit["episode"]
        if len(by_id) == 1:
            return next(iter(by_id.values())), "episode id"
        if len(by_id) > 1:
            raise Unresolved(f"the episode ids point at {len(by_id)} different Trakt episodes")
        aired = parse_time(item.get("PremiereDate"))
        if aired is None:
            raise Unresolved("Trakt knows none of its ids and Jellyfin has no air date for it")
        if show not in self.seasons:
            self.seasons[show] = [
                e
                for s in self.trakt.call("GET", f"/shows/{show}/seasons", {"extended": "full,episodes"}) or []
                for e in s.get("episodes") or []
            ]
        near = [
            e
            for e in self.seasons[show]
            if (t := parse_time(e.get("first_aired"))) and abs((t.date() - aired.date()).days) <= 1
        ]
        same = [
            e
            for e in near
            if (e.get("season"), e.get("number")) == (item.get("ParentIndexNumber"), item.get("IndexNumber"))
        ]
        if len(same) == 1:
            return same[0], "air date and number"
        if len(near) == 1:
            return near[0], "air date"
        titled = [e for e in near if norm_title(e.get("title") or "") == norm_title(item.get("Name") or "")]
        if len(titled) == 1:
            return titled[0], "air date and title"
        raise Unresolved(f"{len(near)} Trakt episodes aired within a day of {aired.date()}")


def recent_played(jellyfin: Api, user_id: str, cutoff: datetime) -> list[tuple[dict[str, Any], datetime]]:
    """Played episodes with their last play, most recent first, until the last play falls before the cutoff."""
    found: list[tuple[dict[str, Any], datetime]] = []
    start = 0
    while True:
        page = jellyfin.call(
            "GET",
            "/Items",
            {
                "userId": user_id,
                "IsPlayed": "true",
                "IncludeItemTypes": "Episode",
                "Recursive": "true",
                "SortBy": "DatePlayed",
                "SortOrder": "Descending",
                "Fields": "ProviderIds,PremiereDate,Path",
                "EnableImages": "false",
                "StartIndex": str(start),
                "Limit": "100",
            },
        )
        items = page.get("Items") or []
        for item in items:
            played = parse_time((item.get("UserData") or {}).get("LastPlayedDate"))
            if played is None or played < cutoff:
                return found
            found.append((item, played))
        start += len(items)
        if not items or start >= int(page.get("TotalRecordCount") or 0):
            return found


def stop_times(jellyfin: Api, cutoff: datetime) -> dict[tuple[str, str], datetime]:
    """Latest playback-stopped activity entry per (user, item) since the cutoff."""
    entries = jellyfin.call(
        "GET", "/System/ActivityLog/Entries", {"minDate": trakt_time(cutoff), "hasUserId": "true", "limit": "5000"}
    )
    latest: dict[tuple[str, str], datetime] = {}
    for entry in entries.get("Items") or []:
        when = parse_time(entry.get("Date"))
        if entry.get("Type") == "VideoPlaybackStopped" and entry.get("ItemId") and when:
            key = (str(entry.get("UserId", "")).replace("-", "").lower(), str(entry["ItemId"]).replace("-", "").lower())
            latest[key] = max(when, latest.get(key, when))
    return latest


def label(item: dict[str, Any]) -> str:
    return f"{item.get('SeriesName')} S{item.get('ParentIndexNumber') or 0:02d}E{item.get('IndexNumber') or 0:02d} '{item.get('Name')}'"


def bind_account(state: dict[str, Any], user: TraktUser, account: str) -> int:
    """Cached results hold only for the Trakt account that produced them. When the plugin is linked to another
    account (or the account was never recorded, as before 2026-10-05), drop this user's cached results so every
    play is checked against the account in use. Returns how many were dropped."""
    accounts = state.setdefault("accounts", {})
    if accounts.get(user.jellyfin_id) == account:
        return 0
    prefix = f"{user.jellyfin_id}:"
    dropped = 0
    for bucket in ("pending", "done", "alerted"):
        keep = {k: v for k, v in state.get(bucket, {}).items() if not k.startswith(prefix)}
        dropped += len(state.get(bucket, {})) - len(keep)
        state[bucket] = keep
    log(
        "INFO",
        f"Jellyfin user {user.jellyfin_id} is linked to Trakt account {account} (was {accounts.get(user.jellyfin_id)}): {dropped} cached results dropped",
    )
    accounts[user.jellyfin_id] = account
    return dropped


def fill(
    jellyfin: Api,
    users: list[tuple[TraktUser, Api]],
    state: dict[str, Any],
    apply: bool,
    clock: Clock,
    notify: Callable[[str], None],
    lookback: float,
    settle: float,
    since: datetime | None,
    post_spacing: Callable[[], None],
) -> dict[str, int]:
    now = clock()
    cutoff = datetime.fromtimestamp(now - lookback, timezone.utc)
    if since and since > cutoff:
        cutoff = since
    for bucket in ("pending", "done", "alerted"):
        state[bucket] = {k: v for k, v in state.get(bucket, {}).items() if float(v) >= now - lookback - 86400.0}
    tally = {"played": 0, "on_trakt": 0, "pending": 0, "would_add": 0, "added": 0, "unresolved": 0, "excluded": 0}
    stops = stop_times(jellyfin, cutoff)
    series_ids: dict[str, dict[str, str]] = {}
    for user, trakt in users:
        bind_account(state, user, str(trakt.call("GET", "/users/settings")["user"]["ids"]["slug"]))
        resolver = Resolver(trakt)
        for item, played in recent_played(jellyfin, user.jellyfin_id, cutoff):
            tally["played"] += 1
            path = item.get("Path") or ""
            if any(path == root or path.startswith(root + "/") for root in user.excluded):
                tally["excluded"] += 1
                continue
            key = f"{user.jellyfin_id}:{item['Id']}:{trakt_time(played)}"
            if key in state["done"]:
                tally["on_trakt"] += 1
                continue
            try:
                sid = item.get("SeriesId") or ""
                if sid not in series_ids:
                    series_ids[sid] = (jellyfin.call("GET", f"/Items/{sid}", {"userId": user.jellyfin_id}) or {}).get(
                        "ProviderIds"
                    ) or {}
                episode, how = resolver.episode(resolver.show(series_ids[sid]), item)
            except Unresolved as reason:
                tally["unresolved"] += 1
                if key not in state["alerted"]:
                    log("WARN", f"{label(item)}: not matched on Trakt, left alone: {reason}")
                    if apply:
                        state["alerted"][key] = now
                        notify(
                            f"<b>Trakt gap filler</b>: could not match\n{html.escape(label(item))}\n{html.escape(str(reason))}"
                        )
                continue
            target = f"Trakt S{episode['season']:02d}E{episode['number']:02d} '{episode.get('title')}' (id {episode['ids']['trakt']}, by {how})"
            if trakt.call("GET", f"/sync/history/episodes/{episode['ids']['trakt']}", {"limit": "1"}):
                tally["on_trakt"] += 1
                state["done"][key] = now
                state["pending"].pop(key, None)
                log("INFO", f"{label(item)} -> {target}: already on Trakt")
                continue
            first = float(state["pending"].setdefault(key, now))
            if now - first < settle:
                tally["pending"] += 1
                log("INFO", f"{label(item)} -> {target}: missing on Trakt; added if still missing after {settle:.0f} s")
                continue
            watched = stops.get((user.jellyfin_id, str(item["Id"]).lower()), played)
            watched = max(watched, played)
            if not apply:
                tally["would_add"] += 1
                log("INFO", f"dry run: would add {label(item)} -> {target}, watched {trakt_time(watched)}")
                continue
            post_spacing()
            reply = trakt.call(
                "POST",
                "/sync/history",
                body={"episodes": [{"ids": {"trakt": episode["ids"]["trakt"]}, "watched_at": trakt_time(watched)}]},
            )
            if (reply or {}).get("added", {}).get("episodes") != 1:
                raise ApiError(f"Trakt did not add {label(item)} -> {target}: {reply}")
            tally["added"] += 1
            state["done"][key] = now
            state["pending"].pop(key, None)
            log("INFO", f"added {label(item)} -> {target}, watched {trakt_time(watched)}")
            notify(f"<b>Trakt gap filled</b>: {html.escape(label(item))}\n-> {html.escape(target)}")
    return tally


def load_state(path: str) -> dict[str, Any]:
    try:
        with open(path, encoding="utf-8") as handle:
            data = json.load(handle)
    except FileNotFoundError:
        return {}
    if not isinstance(data, dict):
        raise TypeError(f"{path} does not hold a JSON object")
    return data


def save_state(path: str, state: dict[str, Any]) -> None:
    temporary = path + ".tmp"
    with open(temporary, "w", encoding="utf-8") as handle:
        json.dump(state, handle, indent=1, sort_keys=True)
    os.replace(temporary, path)


def main(argv: list[str]) -> int:
    parser = argparse.ArgumentParser(
        description="Add the episode plays the Jellyfin Trakt plugin fails to send to Trakt."
    )
    parser.add_argument("--apply", action="store_true", help="add plays; without it only log what would be added")
    args = parser.parse_args(argv)
    token = os.environ.get("JELLYFIN_API_TOKEN", "")
    plugins = Path(os.environ.get("JELLYFIN_PLUGINS_DIR", ""))
    if not token or not (plugins / "configurations" / "Trakt.xml").is_file():
        log("ERROR", "set JELLYFIN_API_TOKEN, and JELLYFIN_PLUGINS_DIR to the folder holding configurations/Trakt.xml")
        return 2
    since = parse_time(os.environ.get("TRAKT_GAP_FILLER_SINCE"))
    lookback = float(os.environ.get("TRAKT_GAP_FILLER_LOOKBACK_HOURS", "72")) * 3600.0
    settle = float(os.environ.get("TRAKT_GAP_FILLER_SETTLE_MINUTES", "10")) * 60.0
    os.makedirs(STATE_DIR, exist_ok=True)
    with open(os.path.join(STATE_DIR, "run.lock"), "w", encoding="utf-8") as lock:
        try:
            fcntl.flock(lock, fcntl.LOCK_EX | fcntl.LOCK_NB)
        except BlockingIOError:
            log("INFO", "another run holds the lock; skipping")
            return 0
        state_path = os.path.join(STATE_DIR, "state.json")
        state = load_state(state_path)
        try:
            users = read_trakt_users(plugins / "configurations" / "Trakt.xml")
            if not users:
                log("INFO", "no Jellyfin user is linked to Trakt with scrobbling or set-watched on")
                return 0
            client = os.environ.get("TRAKT_CLIENT_ID") or state.get("client_id") or ""
            if not client:

                def probe(candidate: str) -> bool:
                    try:
                        Http(
                            TRAKT_URL,
                            {
                                "trakt-api-version": "2",
                                "trakt-api-key": candidate,
                                "Authorization": f"Bearer {users[0].token}",
                            },
                            0.35,
                        ).call("GET", "/users/settings")
                    except ApiError:
                        return False
                    return True

                client = pick_client_id(plugin_client_ids(plugins), probe)
                state["client_id"] = client
            trakt_users: list[tuple[TraktUser, Api]] = [
                (
                    u,
                    Http(
                        TRAKT_URL,
                        {"trakt-api-version": "2", "trakt-api-key": client, "Authorization": f"Bearer {u.token}"},
                        0.35,
                    ),
                )
                for u in users
            ]
            jellyfin = Http(
                os.environ.get("JELLYFIN_URL", "http://127.0.0.1:8096"),
                {"Authorization": f'MediaBrowser Token="{token}"'},
                0.0,
            )
            tally = fill(
                jellyfin,
                trakt_users,
                state,
                args.apply,
                time.time,
                send_telegram if args.apply else lambda _: None,
                lookback,
                settle,
                since,
                lambda: time.sleep(1.1),
            )
        except ApiError as error:
            if "HTTP 403" in str(error):  # a stale cached client id: probe again next run
                state.pop("client_id", None)
            save_state(state_path, state)
            log("ERROR", str(error))
            if args.apply:
                send_telegram(f"<b>{NAME} failed</b>: {html.escape(str(error))}")
            return 1
        save_state(state_path, state)
    mode = "apply" if args.apply else "dry-run"
    log("INFO", f"{mode}: " + ", ".join(f"{name}={count}" for name, count in tally.items()))
    return 0


if __name__ == "__main__":
    sys.exit(main(sys.argv[1:]))
