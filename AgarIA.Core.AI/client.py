"""REST client for the .NET game server AI API."""

import requests
from config import API_URL


class GameClient:
    def __init__(self, base_url: str = API_URL):
        self.base_url = base_url.rstrip("/")
        self.session = requests.Session()

    def get_state(self) -> dict:
        resp = self.session.get(f"{self.base_url}/api/ai/state")
        resp.raise_for_status()
        return resp.json()

    def get_config(self) -> dict:
        resp = self.session.get(f"{self.base_url}/api/ai/config")
        resp.raise_for_status()
        return resp.json()

    def register_bots(self, count: int) -> list[str]:
        resp = self.session.post(
            f"{self.base_url}/api/ai/players",
            json={"count": count},
        )
        resp.raise_for_status()
        return resp.json()["playerIds"]

    def remove_bots(self):
        resp = self.session.delete(f"{self.base_url}/api/ai/players")
        resp.raise_for_status()

    def post_actions(self, actions: list[dict]):
        resp = self.session.post(
            f"{self.base_url}/api/ai/actions",
            json={"actions": actions},
        )
        resp.raise_for_status()
        return resp.json()

    def get_training_mode(self) -> bool:
        resp = self.session.get(f"{self.base_url}/api/ai/training")
        resp.raise_for_status()
        return resp.json()["enabled"]

    def post_stats(self, stats: dict):
        resp = self.session.post(
            f"{self.base_url}/api/ai/stats",
            json=stats,
        )
        resp.raise_for_status()
