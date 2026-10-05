"""Tests for the Trakt gap filler, against fake Jellyfin and Trakt APIs built from the 2026-10-05 case."""

from __future__ import annotations

import copy
import fcntl
import importlib.util
import io
import json
import os
import sys
import tempfile
import unittest
import urllib.parse
from contextlib import redirect_stderr
from datetime import datetime, timedelta, timezone
from pathlib import Path
from typing import Any
from unittest import mock

SCRIPT = Path(__file__).resolve().parent / "trakt_gap_filler.py"
SPEC = importlib.util.spec_from_file_location("trakt_gap_filler", SCRIPT)
assert SPEC is not None and SPEC.loader is not None
MOD = importlib.util.module_from_spec(SPEC)
sys.modules[SPEC.name] = MOD
SPEC.loader.exec_module(MOD)

USER = "51581cf5c622429cae8e736004f5f7be"
SERIES_ID = "99eff4aa7cd418d1900db292eb23940e"
SERIES_IDS = {"AniDB": "18026", "Imdb": "tt31975847", "Tmdb": "237150", "Tvdb": "434704"}
SHOW = 213134
# The episode of 2026-10-05 as Jellyfin reported it: TVDB order S03E02, ids Trakt did not know yet.
ITEM: dict[str, Any] = {
    "Id": "cf7b7df6993d5fe3e713dbb5dd8eb92e",
    "Name": "Episode 2",
    "SeriesName": "As a Reincarnated Aristocrat, I'll Use My Appraisal Skill to Rise in the World",
    "SeriesId": SERIES_ID,
    "ParentIndexNumber": 3,
    "IndexNumber": 2,
    "PremiereDate": "2026-10-04T00:00:00.0000000Z",
    "ProviderIds": {"AniDB": "317012", "Imdb": "tt47664785", "Tvdb": "12004783"},
    "Path": "/media/anime/Reincarnated Aristocrat/Season 3/S03E02 - Canarre in Crisis WEBDL-1080p.mkv",
    "UserData": {"Played": True, "LastPlayedDate": "2026-10-05T05:29:52.2253719Z"},
}
STOP = "2026-10-05T05:32:51.3861957Z"


def trakt_episode(number: int, title: str, aired: str, trakt_id: int, season: int = 1) -> dict[str, Any]:
    return {"season": season, "number": number, "title": title, "first_aired": aired, "ids": {"trakt": trakt_id}}


def history_row(season: int, number: int, watched: str, trakt_id: int, action: str = "scrobble") -> dict[str, Any]:
    return {
        "watched_at": watched,
        "action": action,
        "type": "episode",
        "episode": {"season": season, "number": number, "ids": {"trakt": trakt_id}},
    }


# Trakt's TMDB-ordered season 1 around the new episodes, as read on 2026-10-05.
SEASONS: list[dict[str, Any]] = [
    {
        "number": 1,
        "episodes": [
            trakt_episode(24, "Entrusted Feelings", "2024-12-22T14:30:00.000Z", 12319685),
            trakt_episode(25, "Headman", "2026-09-27T14:30:00.000Z", 14474988),
            trakt_episode(26, "Canarre in Crisis", "2026-10-04T14:30:00.000Z", 14531280),
            trakt_episode(27, "Zaht and Braham", "2026-10-11T14:30:00.000Z", 14547925),
        ],
    }
]


# The second case of 2026-10-05: Overgeared S01E02. The plugin scrobbled it by show plus S01E02, but Trakt's copy
# held none of Jellyfin's ids and was dated 2026-10-09 against TVDB's 2026-10-04.
OG_SERIES_ID = "3ac2e3af8c4e22b9c077e7457ebf5957"
OG_SERIES_IDS = {"AniDB": "20077", "Imdb": "tt43691353", "Tmdb": "324502", "Tvdb": "478752"}
OG_SHOW = 319961
OG_ITEM: dict[str, Any] = {
    "Id": "81562ef833ffd2bebc23c2648b9c1d5b",
    "Name": "Episode 2",
    "SeriesName": "Overgeared",
    "SeriesId": OG_SERIES_ID,
    "ParentIndexNumber": 1,
    "IndexNumber": 2,
    "PremiereDate": "2026-10-04T00:00:00.0000000Z",
    "ProviderIds": {"AniDB": "317009", "Imdb": "tt47663851", "Tvdb": "12004882"},
    "Path": "/media/anime/Overgeared/Season 1/Overgeared - S01E02 - A Blacksmith's First Step WEBDL-1080p.mkv",
    "UserData": {"Played": True, "LastPlayedDate": "2026-10-05T09:16:45.6989066Z"},
}
OG_STOP = "2026-10-05T09:27:09.4264107Z"
OG_SEASONS: list[dict[str, Any]] = [
    {
        "number": 1,
        "episodes": [
            trakt_episode(1, "Legendary Class", "2026-10-02T14:30:00.000Z", 14224604),
            trakt_episode(2, "The Power of Items", "2026-10-09T14:30:00.000Z", 14531117),
            trakt_episode(3, "Episode 3", "2026-10-16T14:30:00.000Z", 14531118),
        ],
    }
]
OG_SCROBBLE = history_row(1, 2, "2026-10-05T09:27:00.000Z", 14531117)
OG_OTHER = history_row(1, 1, "2026-10-05T09:29:00.000Z", 14224604, "watch")


def at(iso: str) -> float:
    return datetime.fromisoformat(iso).timestamp()


def utc(iso: str) -> datetime:
    return datetime.fromisoformat(iso.replace("Z", "+00:00"))


class FakeJellyfin:
    def __init__(self) -> None:
        self.items = [copy.deepcopy(ITEM)]
        self.series = {SERIES_ID: {"ProviderIds": dict(SERIES_IDS)}}
        self.activity = [{"Date": STOP, "Type": "VideoPlaybackStopped", "ItemId": ITEM["Id"], "UserId": USER}]
        self.calls: list[tuple[str, str, dict[str, str] | None]] = []

    def call(self, method: str, path: str, query: dict[str, str] | None = None, body: Any = None) -> Any:
        self.calls.append((method, path, query))
        assert method == "GET" and body is None
        if path == "/Items":
            assert query is not None and query["userId"] == USER and query["IsPlayed"] == "true"
            assert query["SortBy"] == "DatePlayed" and query["SortOrder"] == "Descending"
            start, limit = int(query["StartIndex"]), int(query["Limit"])
            return {"Items": copy.deepcopy(self.items[start : start + limit]), "TotalRecordCount": len(self.items)}
        if path.startswith("/Items/"):
            return copy.deepcopy(self.series[path.split("/")[2]])
        if path == "/System/ActivityLog/Entries":
            return {"Items": copy.deepcopy(self.activity)}
        raise AssertionError(f"unexpected Jellyfin call {path}")


class FakeTrakt:
    def __init__(self) -> None:
        self.search: dict[tuple[str, str, str], list[dict[str, Any]]] = {
            (source, SERIES_IDS[key], "show"): [{"type": "show", "show": {"ids": {"trakt": SHOW}}}]
            for key, source in (("Tvdb", "tvdb"), ("Tmdb", "tmdb"), ("Imdb", "imdb"))
        }
        self.seasons = {SHOW: copy.deepcopy(SEASONS)}
        self.history: dict[int, list[dict[str, Any]]] = {}
        self.show_history: dict[int, list[dict[str, Any]]] = {}
        self.history_queries: list[dict[str, str]] = []
        self.reply: Any = {"added": {"movies": 0, "episodes": 1}, "not_found": {"episodes": []}}
        self.posts: list[Any] = []
        self.calls: list[tuple[str, str]] = []
        self.slug = "vic-new"

    def call(self, method: str, path: str, query: dict[str, str] | None = None, body: Any = None) -> Any:
        self.calls.append((method, path))
        if method == "POST" and path == "/sync/history":
            self.posts.append(copy.deepcopy(body))
            return copy.deepcopy(self.reply)
        assert method == "GET"
        if path == "/users/settings":
            return {"user": {"ids": {"slug": self.slug}}}
        if path.startswith("/search/"):
            _, _, source, ident = path.split("/")
            assert query is not None
            return copy.deepcopy(self.search.get((source, urllib.parse.unquote(ident), query["type"]), []))
        if path.startswith("/shows/") and path.endswith("/seasons"):
            assert query == {"extended": "full,episodes"}
            return copy.deepcopy(self.seasons[int(path.split("/")[2])])
        if path.startswith("/sync/history/episodes/"):
            return copy.deepcopy(self.history.get(int(path.rsplit("/", 1)[1]), []))
        if path.startswith("/sync/history/shows/"):
            # As Trakt answered on 2026-10-05: dates are read as midnight UTC, the end is exclusive, pages of limit.
            assert query is not None and set(query) == {"start_at", "end_at", "page", "limit"}
            self.history_queries.append(dict(query))
            start, end = utc(query["start_at"] + "T00:00:00Z"), utc(query["end_at"] + "T00:00:00Z")
            rows = [
                r for r in self.show_history.get(int(path.rsplit("/", 1)[1]), []) if start <= utc(r["watched_at"]) < end
            ]
            page, limit = int(query["page"]), int(query["limit"])
            return copy.deepcopy(rows[(page - 1) * limit : page * limit])
        raise AssertionError(f"unexpected Trakt call {path}")


class FillBase(unittest.TestCase):
    def setUp(self) -> None:
        patcher = mock.patch("urllib.request.urlopen", side_effect=AssertionError("tests must not reach the network"))
        patcher.start()
        self.addCleanup(patcher.stop)
        self.jellyfin = FakeJellyfin()
        self.trakt = FakeTrakt()
        self.user = MOD.TraktUser(USER, "token", [])
        self.state: dict[str, Any] = {}
        self.notes: list[str] = []
        self.spaced = 0
        self.now = at("2026-10-05T06:00:00+00:00")

    def space(self) -> None:
        self.spaced += 1

    def run_fill(
        self, apply: bool = True, since: datetime | None = None, lookback: float = 72 * 3600.0
    ) -> dict[str, int]:
        with redirect_stderr(io.StringIO()) as err:
            tally: dict[str, int] = MOD.fill(
                self.jellyfin,
                [(self.user, self.trakt)],
                self.state,
                apply,
                lambda: self.now,
                self.notes.append,
                lookback,
                600.0,
                since,
                self.space,
            )
        self.log = err.getvalue()
        return tally


class FillTests(FillBase):
    def test_regression_missed_episode_is_added_once_after_settling_with_the_stop_time(self) -> None:
        first = self.run_fill()
        self.assertEqual((first["pending"], first["added"], self.trakt.posts), (1, 0, []))
        self.now += 599.0
        self.assertEqual(self.run_fill()["pending"], 1)
        self.now += 1.0
        second = self.run_fill()
        self.assertEqual(second["added"], 1)
        self.assertEqual(
            self.trakt.posts,
            [{"episodes": [{"ids": {"trakt": 14531280}, "watched_at": "2026-10-05T05:32:51.000Z"}]}],
        )
        self.assertEqual(self.spaced, 1)
        self.assertIn("by air date", self.log)
        self.assertEqual(len(self.notes), 1)
        self.assertIn("S01E26", self.notes[0])
        self.now += 900.0
        third = self.run_fill()
        self.assertEqual((third["on_trakt"], third["added"], len(self.trakt.posts)), (1, 0, 1))

    def test_dry_run_never_posts_or_notifies(self) -> None:
        self.run_fill(apply=False)
        self.now += 600.0
        tally = self.run_fill(apply=False)
        self.assertEqual((tally["would_add"], self.trakt.posts, self.notes), (1, [], []))

    def test_an_episode_trakt_already_has_is_left_alone(self) -> None:
        self.trakt.history[14531280] = [{"id": 1, "watched_at": "2026-10-05T05:32:00.000Z"}]
        tally = self.run_fill()
        self.assertEqual((tally["on_trakt"], tally["pending"], self.trakt.posts), (1, 0, []))
        self.assertEqual(self.state["pending"], {})

    def test_an_episode_id_match_must_belong_to_the_same_show(self) -> None:
        foreign = {
            "type": "episode",
            "show": {"ids": {"trakt": 999}},
            "episode": {"season": 3, "number": 2, "ids": {"trakt": 5}},
        }
        self.trakt.search[("tvdb", "12004783", "episode")] = [foreign]
        self.run_fill()
        self.now += 600.0
        self.run_fill()
        self.assertEqual(self.trakt.posts[0]["episodes"][0]["ids"]["trakt"], 14531280)
        own = {
            "type": "episode",
            "show": {"ids": {"trakt": SHOW}},
            "episode": {"season": 1, "number": 26, "title": "Canarre in Crisis", "ids": {"trakt": 14531280}},
        }
        self.trakt.search[("imdb", "tt47664785", "episode")] = [own]
        self.state = {}
        self.trakt.posts = []
        self.run_fill()
        self.assertIn("by episode id", self.log)
        self.assertNotIn(("GET", f"/shows/{SHOW}/seasons"), self.trakt.calls[-3:])

    def test_episode_ids_naming_two_episodes_are_unresolved(self) -> None:
        for source, ident, trakt_id in (("tvdb", "12004783", 1), ("imdb", "tt47664785", 2)):
            self.trakt.search[(source, ident, "episode")] = [
                {
                    "type": "episode",
                    "show": {"ids": {"trakt": SHOW}},
                    "episode": {"season": 1, "number": trakt_id, "ids": {"trakt": trakt_id}},
                }
            ]
        self.assertEqual(self.run_fill()["unresolved"], 1)
        self.assertIn("2 different Trakt episodes", self.log)

    def test_the_same_number_wins_among_episodes_aired_within_a_day(self) -> None:
        self.trakt.seasons[SHOW][0]["episodes"].append(
            trakt_episode(2, "Other", "2026-10-04T15:00:00.000Z", 777, season=3)
        )
        self.run_fill()
        self.now += 600.0
        self.run_fill()
        self.assertEqual(self.trakt.posts[0]["episodes"][0]["ids"]["trakt"], 777)
        self.assertIn("by air date and number", self.log)

    def test_the_title_breaks_a_tie_when_numbers_differ(self) -> None:
        self.jellyfin.items[0]["Name"] = "Canarre in Crisis!"
        self.trakt.seasons[SHOW][0]["episodes"].append(
            trakt_episode(40, "Something Else", "2026-10-05T01:00:00.000Z", 778)
        )
        self.run_fill()
        self.now += 600.0
        self.run_fill()
        self.assertEqual(self.trakt.posts[0]["episodes"][0]["ids"]["trakt"], 14531280)
        self.assertIn("by air date and title", self.log)

    def test_two_episodes_aired_within_a_day_without_a_tiebreak_are_alerted_once_and_left_alone(self) -> None:
        self.trakt.seasons[SHOW][0]["episodes"].append(
            trakt_episode(40, "Something Else", "2026-10-05T01:00:00.000Z", 778)
        )
        self.assertEqual(self.run_fill()["unresolved"], 1)
        self.now += 900.0
        self.assertEqual(self.run_fill()["unresolved"], 1)
        self.assertEqual((len(self.notes), self.trakt.posts), (1, []))
        self.assertIn("2 Trakt episodes aired within a day of 2026-10-04", self.notes[0])

    def test_unresolved_in_a_dry_run_is_logged_but_not_remembered(self) -> None:
        self.jellyfin.items[0]["PremiereDate"] = None
        self.run_fill(apply=False)
        self.assertEqual((self.state["alerted"], self.notes), ({}, []))
        self.assertIn("no air date", self.log)

    def test_air_dates_match_within_one_day_only(self) -> None:
        self.jellyfin.items[0]["PremiereDate"] = "2026-10-05T00:00:00.0000000Z"
        self.run_fill()
        self.assertEqual(self.state["pending"] != {}, True)
        self.state = {}
        self.jellyfin.items[0]["PremiereDate"] = "2026-10-06T00:00:00.0000000Z"
        self.assertEqual(self.run_fill()["unresolved"], 1)
        self.assertIn("0 Trakt episodes aired within a day of 2026-10-06", self.log)

    def test_series_ids_that_name_different_shows_are_unresolved(self) -> None:
        self.trakt.search[("tmdb", "237150", "show")] = [{"type": "show", "show": {"ids": {"trakt": 999}}}]
        self.assertEqual(self.run_fill()["unresolved"], 1)
        self.assertIn("match 2 Trakt shows", self.log)

    def test_an_excluded_location_is_skipped(self) -> None:
        self.user = MOD.TraktUser(USER, "token", ["/media/anime/Reincarnated Aristocrat"])
        tally = self.run_fill()
        self.assertEqual(
            (tally["excluded"], self.trakt.calls), (1, [("GET", "/users/settings")])
        )  # only the account check
        self.user = MOD.TraktUser(USER, "token", ["/media/anime/Reincarnated"])
        self.assertEqual(self.run_fill()["excluded"], 0)

    def test_plays_before_since_or_the_lookback_are_not_listed(self) -> None:
        self.assertEqual(self.run_fill(since=datetime.fromisoformat("2026-10-05T05:30:00+00:00"))["played"], 0)
        self.assertEqual(self.run_fill(lookback=1800.0)["played"], 0)
        self.assertEqual(self.run_fill(since=datetime.fromisoformat("2026-10-04T03:34:00+00:00"))["played"], 1)

    def test_jellyfin_listing_pages_until_its_total_and_stops_at_the_cutoff(self) -> None:
        older = copy.deepcopy(ITEM)
        older["UserData"]["LastPlayedDate"] = "2026-09-01T00:00:00Z"
        self.jellyfin.items = [copy.deepcopy(ITEM) for _ in range(150)]
        for i, item in enumerate(self.jellyfin.items):
            item["Id"] = f"{i:032x}"
        self.assertEqual(
            len(MOD.recent_played(self.jellyfin, USER, datetime.fromisoformat("2026-10-01T00:00:00+00:00"))), 150
        )
        self.assertEqual([(c[2] or {})["StartIndex"] for c in self.jellyfin.calls if c[1] == "/Items"], ["0", "100"])
        self.jellyfin.items[1] = older
        self.jellyfin.calls = []
        self.assertEqual(
            len(MOD.recent_played(self.jellyfin, USER, datetime.fromisoformat("2026-10-01T00:00:00+00:00"))), 1
        )
        self.assertEqual(len([c for c in self.jellyfin.calls if c[1] == "/Items"]), 1)

    def test_a_stop_entry_older_than_the_last_play_is_not_used(self) -> None:
        self.jellyfin.activity[0]["Date"] = "2026-10-05T05:00:00Z"
        self.run_fill()
        self.now += 600.0
        self.run_fill()
        self.assertEqual(self.trakt.posts[0]["episodes"][0]["watched_at"], "2026-10-05T05:29:52.000Z")

    def test_trakt_not_adding_the_play_is_an_error(self) -> None:
        self.trakt.reply = {"added": {"episodes": 0}, "not_found": {"episodes": [{"ids": {"trakt": 14531280}}]}}
        self.run_fill()
        self.now += 600.0
        with self.assertRaises(MOD.ApiError):
            self.run_fill()
        self.assertNotIn(f"{USER}:{ITEM['Id']}:2026-10-05T05:29:52.000Z", self.state["done"])

    def test_old_state_entries_are_pruned(self) -> None:
        self.state = {"pending": {"old": self.now - 5 * 86400.0, "new": self.now}, "done": {}, "alerted": {"old": 1.0}}
        self.run_fill()
        self.assertNotIn("old", self.state["pending"])
        self.assertIn("new", self.state["pending"])
        self.assertEqual(self.state["alerted"], {})


class AccountBindingTests(FillBase):
    KEY = f"{USER}:{ITEM['Id']}:2026-10-05T05:29:52.000Z"
    OTHER = "00000000000000000000000000000002:abc:2026-10-05T05:00:00.000Z"

    def test_a_new_trakt_account_drops_this_users_cache_and_rechecks_live(self) -> None:
        self.trakt.history[14531280] = [{"id": 1}]
        self.state = {
            "accounts": {USER: "old-account"},
            "done": {self.KEY: self.now, self.OTHER: self.now},
            "pending": {},
            "alerted": {},
        }
        tally = self.run_fill()
        self.assertEqual(tally["on_trakt"], 1)
        self.assertIn(("GET", "/sync/history/episodes/14531280"), self.trakt.calls)  # checked live, not from the cache
        self.assertEqual(self.state["accounts"][USER], "vic-new")
        self.assertIn(self.OTHER, self.state["done"])  # another Jellyfin user's results are untouched
        self.assertIn("1 cached results dropped", self.log)

    def test_the_same_account_keeps_the_cache(self) -> None:
        self.state = {"accounts": {USER: "vic-new"}, "done": {self.KEY: self.now}, "pending": {}, "alerted": {}}
        tally = self.run_fill()
        self.assertEqual(tally["on_trakt"], 1)
        self.assertNotIn(("GET", "/sync/history/episodes/14531280"), self.trakt.calls)
        self.assertNotIn("cached results dropped", self.log)

    def test_results_cached_before_accounts_were_recorded_are_rechecked(self) -> None:
        self.trakt.history[14531280] = [{"id": 1}]
        self.state = {"done": {self.KEY: self.now}, "pending": {}, "alerted": {}}
        self.run_fill()
        self.assertIn(("GET", "/sync/history/episodes/14531280"), self.trakt.calls)
        self.assertEqual(self.state["accounts"], {USER: "vic-new"})


class PluginScrobbleTests(FillBase):
    """An episode the resolver cannot place counts as delivered when Trakt holds the plugin's own scrobble of it."""

    KEY = f"{USER}:{OG_ITEM['Id']}:2026-10-05T09:16:45.000Z"

    def setUp(self) -> None:
        super().setUp()
        self.now = at("2026-10-05T09:39:20+00:00")
        self.use_times("2026-10-05T09:16:45.6989066Z", OG_STOP)
        self.jellyfin.series = {OG_SERIES_ID: {"ProviderIds": dict(OG_SERIES_IDS)}}
        self.trakt.search = {
            (source, OG_SERIES_IDS[key], "show"): [{"type": "show", "show": {"ids": {"trakt": OG_SHOW}}}]
            for key, source in (("Tvdb", "tvdb"), ("Tmdb", "tmdb"), ("Imdb", "imdb"))
        }
        self.trakt.seasons = {OG_SHOW: copy.deepcopy(OG_SEASONS)}
        self.trakt.show_history = {OG_SHOW: [copy.deepcopy(OG_OTHER), copy.deepcopy(OG_SCROBBLE)]}

    def use_times(self, started: str, stopped: str) -> None:
        item = copy.deepcopy(OG_ITEM)
        item["UserData"]["LastPlayedDate"] = started
        self.jellyfin.items = [item]
        self.jellyfin.activity = [
            {"Date": stopped, "Type": "VideoPlaybackStopped", "ItemId": item["Id"], "UserId": USER}
        ]

    def with_plays(self, *rows: dict[str, Any]) -> dict[str, int]:
        self.state, self.notes = {}, []
        self.trakt.show_history = {OG_SHOW: [copy.deepcopy(r) for r in rows]}
        return self.run_fill()

    def test_regression_the_plugins_scrobble_at_jellyfins_number_counts_as_delivered(self) -> None:
        tally = self.run_fill()
        self.assertEqual((tally["on_trakt"], tally["unresolved"], tally["pending"]), (1, 0, 0))
        self.assertEqual((self.notes, self.trakt.posts, self.state["alerted"]), ([], [], {}))
        self.assertEqual(self.state["done"], {self.KEY: self.now})
        self.assertIn(
            "Overgeared S01E02 'Episode 2': 0 Trakt episodes aired within a day of 2026-10-04, but Trakt holds the "
            "plugin's scrobble of it at 2026-10-05T09:27:00.000Z: already on Trakt",
            self.log,
        )
        self.assertEqual(
            self.trakt.history_queries,
            [{"start_at": "2026-10-05", "end_at": "2026-10-06", "page": "1", "limit": "100"}],
        )
        self.now += 900.0
        self.assertEqual(self.run_fill()["on_trakt"], 1)
        self.assertEqual(len(self.trakt.history_queries), 1)

    def test_without_that_scrobble_the_episode_is_still_reported(self) -> None:
        tally = self.with_plays(OG_OTHER)
        self.assertEqual((tally["unresolved"], tally["on_trakt"], len(self.notes), self.trakt.posts), (1, 0, 1, []))
        self.assertIn("0 Trakt episodes aired within a day of 2026-10-04", self.notes[0])
        self.assertEqual(self.state["done"], {})

    def test_a_play_at_another_season_or_number_does_not_count(self) -> None:
        for season, number in ((2, 2), (1, 3)):
            with self.subTest(season=season, number=number):
                tally = self.with_plays(history_row(season, number, "2026-10-05T09:27:00.000Z", 1))
                self.assertEqual((tally["unresolved"], tally["on_trakt"]), (1, 0))

    def test_the_window_runs_from_15_minutes_before_the_start_to_15_minutes_after_the_stop(self) -> None:
        # Start 09:16:45.698 and stop 09:27:09.426, so the window is 09:01:45.698 to 09:42:09.426.
        for watched, counted in (
            ("2026-10-05T09:01:45.000Z", False),
            ("2026-10-05T09:01:46.000Z", True),
            ("2026-10-05T09:42:09.000Z", True),
            ("2026-10-05T09:42:10.000Z", False),
        ):
            with self.subTest(watched=watched):
                tally = self.with_plays(history_row(1, 2, watched, 14531117))
                self.assertEqual((tally["on_trakt"], tally["unresolved"]), (int(counted), int(not counted)))

    def test_a_window_across_midnight_asks_for_both_days(self) -> None:
        self.now = at("2026-10-06T00:30:00+00:00")
        self.use_times("2026-10-05T23:50:00.0000000Z", "2026-10-06T00:05:00.0000000Z")
        tally = self.with_plays(history_row(1, 2, "2026-10-06T00:04:00.000Z", 14531117))
        self.assertEqual(tally["on_trakt"], 1)
        self.assertEqual(
            self.trakt.history_queries[-1],
            {"start_at": "2026-10-05", "end_at": "2026-10-07", "page": "1", "limit": "100"},
        )

    def test_a_scrobble_on_a_later_page_is_found(self) -> None:
        others = [history_row(1, 1, f"2026-10-05T09:2{n // 60}:{n % 60:02d}.000Z", 14224604) for n in range(100)]
        tally = self.with_plays(*others, OG_SCROBBLE)
        self.assertEqual(tally["on_trakt"], 1)
        self.assertEqual([q["page"] for q in self.trakt.history_queries], ["1", "2"])

    def test_paging_stops_at_an_empty_page(self) -> None:
        tally = self.with_plays(OG_OTHER)
        self.assertEqual(tally["unresolved"], 1)
        self.assertEqual([q["page"] for q in self.trakt.history_queries], ["1", "2"])

    def test_a_show_that_does_not_resolve_is_reported_without_asking_for_its_history(self) -> None:
        self.trakt.search = {}
        tally = self.run_fill()
        self.assertEqual((tally["unresolved"], self.trakt.history_queries), (1, []))
        self.assertIn("match 0 Trakt shows", self.notes[0])


class HelperTests(unittest.TestCase):
    def test_parse_time_reads_jellyfin_and_trakt_forms(self) -> None:
        expected = datetime(2026, 10, 5, 5, 29, 52, 225371, tzinfo=timezone.utc)
        self.assertEqual(MOD.parse_time("2026-10-05T05:29:52.2253719Z"), expected)
        self.assertEqual(MOD.parse_time("2026-10-05T05:29:52.225371+00:00"), expected)
        self.assertEqual(MOD.parse_time("2026-10-05T05:29:52Z"), expected.replace(microsecond=0))
        for bad in (None, "", "yesterday", "2026-10-05"):
            self.assertIsNone(MOD.parse_time(bad))
        self.assertEqual(MOD.trakt_time(expected + timedelta(hours=1)), "2026-10-05T06:29:52.000Z")

    def test_read_trakt_users_keeps_posting_users_with_a_token(self) -> None:
        xml = """<?xml version="1.0" encoding="utf-8"?><PluginConfiguration><TraktUsers>
          <TraktUser><AccessToken>tok1</AccessToken><LinkedMbUserId>51581cf5-c622-429c-ae8e-736004f5f7be</LinkedMbUserId>
            <Scrobble>true</Scrobble><PostSetWatched>false</PostSetWatched>
            <LocationsExcluded><string>/media/kids/</string></LocationsExcluded></TraktUser>
          <TraktUser><AccessToken>tok2</AccessToken><LinkedMbUserId>00000000-0000-0000-0000-000000000002</LinkedMbUserId>
            <Scrobble>false</Scrobble><PostSetWatched>false</PostSetWatched></TraktUser>
          <TraktUser><AccessToken></AccessToken><LinkedMbUserId>00000000-0000-0000-0000-000000000003</LinkedMbUserId>
            <Scrobble>true</Scrobble></TraktUser>
        </TraktUsers></PluginConfiguration>"""
        with tempfile.TemporaryDirectory() as folder:
            path = Path(folder) / "Trakt.xml"
            path.write_text(xml, encoding="utf-8")
            self.assertEqual(MOD.read_trakt_users(path), [MOD.TraktUser(USER, "tok1", ["/media/kids"])])

    def test_pick_client_id_needs_exactly_one_accepted_candidate(self) -> None:
        self.assertEqual(MOD.pick_client_id(["a", "b"], lambda c: c == "b"), "b")
        for probe in (lambda c: False, lambda c: True):
            with self.assertRaises(MOD.ApiError):
                MOD.pick_client_id(["a", "b"], probe)

    def test_plugin_client_ids_read_the_newest_plugin_version(self) -> None:
        with tempfile.TemporaryDirectory() as folder:
            for version, hexes in (("9.0.0.0", ["1" * 64]), ("33.0.0.0", ["a" * 64, "b" * 64])):
                (Path(folder) / f"Trakt_{version}").mkdir()
                blob = b"\x00junk" + b"\x01".join(h.encode("utf-16-le") for h in hexes) + b"\x00"
                (Path(folder) / f"Trakt_{version}" / "Trakt.dll").write_bytes(blob)
            self.assertEqual(MOD.plugin_client_ids(Path(folder)), ["a" * 64, "b" * 64])
            with self.assertRaises(MOD.ApiError):
                MOD.plugin_client_ids(Path(folder) / "missing")


class MainTests(unittest.TestCase):
    def setUp(self) -> None:
        patcher = mock.patch("urllib.request.urlopen", side_effect=AssertionError("tests must not reach the network"))
        patcher.start()
        self.addCleanup(patcher.stop)
        self.dir = tempfile.TemporaryDirectory()
        self.addCleanup(self.dir.cleanup)
        plugins = Path(self.dir.name) / "plugins"
        (plugins / "configurations").mkdir(parents=True)
        (plugins / "Trakt_33.0.0.0").mkdir()
        (plugins / "Trakt_33.0.0.0" / "Trakt.dll").write_bytes(
            ("c" * 64).encode("utf-16-le") + b"\x00\x01" + ("d" * 64).encode("utf-16-le")
        )
        (plugins / "configurations" / "Trakt.xml").write_text(
            "<PluginConfiguration><TraktUsers><TraktUser><AccessToken>SECRET-TOKEN</AccessToken>"
            "<LinkedMbUserId>51581cf5-c622-429c-ae8e-736004f5f7be</LinkedMbUserId><Scrobble>true</Scrobble>"
            "</TraktUser></TraktUsers></PluginConfiguration>",
            encoding="utf-8",
        )
        self.env = {"JELLYFIN_API_TOKEN": "jf", "JELLYFIN_PLUGINS_DIR": str(plugins), "TRAKT_CLIENT_ID": ""}
        self.state_dir = Path(self.dir.name) / "state"
        self.jellyfin = FakeJellyfin()
        self.trakt = FakeTrakt()

    def http(self, base: str, headers: dict[str, str], spacing: float) -> Any:
        if base == MOD.TRAKT_URL:
            self.assertEqual(headers["Authorization"], "Bearer SECRET-TOKEN")
            if headers["trakt-api-key"] != "d" * 64:
                return mock.Mock(
                    call=mock.Mock(side_effect=MOD.ApiError("GET /users/settings: HTTP 403: invalid API key"))
                )
            return mock.Mock(
                call=mock.Mock(
                    side_effect=lambda method, path, query=None, body=None: (
                        {"user": {"ids": {"slug": "vic-new"}}}
                        if path == "/users/settings"
                        else self.trakt.call(method, path, query, body)
                    )
                )
            )
        return self.jellyfin

    def test_a_run_picks_the_working_client_id_and_never_writes_the_token(self) -> None:
        err = io.StringIO()
        with (
            mock.patch.dict(os.environ, self.env),
            mock.patch.object(MOD, "STATE_DIR", str(self.state_dir)),
            mock.patch.object(MOD, "Http", side_effect=self.http),
            redirect_stderr(err),
        ):
            self.assertEqual(MOD.main([]), 0)
        state_text = (self.state_dir / "state.json").read_text(encoding="utf-8")
        self.assertEqual(json.loads(state_text)["client_id"], "d" * 64)
        self.assertNotIn("SECRET-TOKEN", state_text + err.getvalue())
        self.assertIn("dry-run: played=1", err.getvalue())

    def test_missing_configuration_refuses_to_run(self) -> None:
        with mock.patch.dict(os.environ, dict(self.env, JELLYFIN_API_TOKEN="")), redirect_stderr(io.StringIO()):
            self.assertEqual(MOD.main([]), 2)

    def test_a_held_lock_skips_cleanly(self) -> None:
        self.state_dir.mkdir()
        with open(self.state_dir / "run.lock", "w", encoding="utf-8") as held:
            fcntl.flock(held, fcntl.LOCK_EX)
            with (
                mock.patch.dict(os.environ, self.env),
                mock.patch.object(MOD, "STATE_DIR", str(self.state_dir)),
                mock.patch.object(MOD, "Http", side_effect=AssertionError("must not call any API")),
                redirect_stderr(io.StringIO()),
            ):
                self.assertEqual(MOD.main([]), 0)

    def test_an_api_failure_exits_1_and_keeps_state(self) -> None:
        self.trakt.reply = {"added": {"episodes": 0}}
        state = {
            "pending": {f"{USER}:{ITEM['Id']}:2026-10-05T05:29:52.000Z": at("2026-10-05T05:45:00+00:00")},
            "accounts": {USER: "vic-new"},
            "client_id": "d" * 64,
        }
        self.state_dir.mkdir()
        (self.state_dir / "state.json").write_text(json.dumps(state), encoding="utf-8")
        with (
            mock.patch.dict(os.environ, self.env),
            mock.patch.object(MOD, "STATE_DIR", str(self.state_dir)),
            mock.patch.object(MOD, "Http", side_effect=self.http),
            mock.patch.object(MOD, "send_telegram") as telegram,
            mock.patch.object(MOD.time, "time", return_value=at("2026-10-05T06:00:00+00:00")),
            mock.patch.object(MOD.time, "sleep"),
            redirect_stderr(io.StringIO()),
        ):
            self.assertEqual(MOD.main(["--apply"]), 1)
        self.assertEqual(json.loads((self.state_dir / "state.json").read_text())["client_id"], "d" * 64)
        self.assertIn("failed", telegram.call_args.args[0])


if __name__ == "__main__":
    unittest.main()
