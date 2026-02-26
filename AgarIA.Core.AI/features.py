"""Feature vector builder from raw game state."""

import math
import numpy as np
from config import OBS_SIZE

TOP_FOOD_K = 64
TOP_PLAYER_K = 32


def build_observations(state: dict, bot_ids: list[str]) -> np.ndarray:
    """Build observation vectors for all bots from raw game state.

    Returns: (num_bots, OBS_SIZE) float32 array.
    """
    players_by_id = {p["id"]: p for p in state["players"]}
    food_list = state["food"]
    map_size = state["mapSize"]

    obs = np.zeros((len(bot_ids), OBS_SIZE), dtype=np.float32)

    for i, bot_id in enumerate(bot_ids):
        bot = players_by_id.get(bot_id)
        if bot is None or not bot["isAlive"]:
            continue
        obs[i] = _build_single(bot, food_list, state["players"], map_size)

    return obs


def _build_single(
    bot: dict,
    food_list: list[dict],
    all_players: list[dict],
    map_size: int,
) -> np.ndarray:
    features = np.zeros(OBS_SIZE, dtype=np.float32)
    idx = 0
    bx, by = bot["x"], bot["y"]
    bot_mass = bot["mass"]
    bot_speed = bot["speed"] if bot["speed"] > 0.01 else 4.0

    # Find global largest mass
    largest_mass = max((p["mass"] for p in all_players), default=bot_mass)

    # Self state (10 features)
    features[idx] = bot_mass / largest_mass if largest_mass > 0 else 1.0
    idx += 1
    features[idx] = 1.0 / bot_mass
    idx += 1
    features[idx] = 1.0 if bot_mass >= 24 else 0.0  # MinSplitMass
    idx += 1
    features[idx] = bx / map_size
    idx += 1
    features[idx] = by / map_size
    idx += 1
    features[idx] = bot["vx"] / bot_speed
    idx += 1
    features[idx] = bot["vy"] / bot_speed
    idx += 1
    features[idx] = 1.0 if bot_speed > 4.0 else 0.0  # speed boost
    idx += 1
    # Second cell (not tracked for simplicity — zero)
    idx += 2  # dx, dy to second cell

    # Food: top 32 by distance (dx, dy only — no distance feature)
    food_dists = []
    for f in food_list:
        dx = f["x"] - bx
        dy = f["y"] - by
        dist_sq = dx * dx + dy * dy
        food_dists.append((dx, dy, dist_sq))

    food_dists.sort(key=lambda t: t[2])

    for j in range(TOP_FOOD_K):
        if j < len(food_dists):
            features[idx] = food_dists[j][0] / 800.0
            idx += 1
            features[idx] = food_dists[j][1] / 800.0
            idx += 1
        else:
            idx += 2

    # Players: top 16 by threat score (dx, dy, relative mass, vx, vy, edibility)
    player_scores = []
    eat_size_ratio = 1.15

    for p in all_players:
        if p["id"] == bot["id"]:
            continue
        if p.get("ownerId") == bot["id"]:
            continue

        dx = p["x"] - bx
        dy = p["y"] - by
        dist = math.sqrt(dx * dx + dy * dy)
        mass_ratio = p["mass"] / bot_mass if bot_mass > 0 else 1.0
        score = mass_ratio / (dist + 1.0)
        player_scores.append((dx, dy, score, p["mass"], p))

    player_scores.sort(key=lambda t: -t[2])

    eat_threshold = bot_mass / eat_size_ratio

    for j in range(TOP_PLAYER_K):
        if j < len(player_scores):
            dx, dy, _, pmass, p = player_scores[j]
            features[idx] = dx / 1600.0
            idx += 1
            features[idx] = dy / 1600.0
            idx += 1
            features[idx] = pmass / largest_mass if largest_mass > 0 else 0.0
            idx += 1
            features[idx] = p["vx"] / bot_speed
            idx += 1
            features[idx] = p["vy"] / bot_speed
            idx += 1
            # Edibility
            if bot_mass > pmass * eat_size_ratio:
                features[idx] = 1.0
            elif pmass > eat_threshold:
                features[idx] = -1.0
            else:
                features[idx] = 0.0
            idx += 1
        else:
            idx += 6

    return features


def compute_rewards(
    prev_state: dict | None,
    curr_state: dict,
    bot_ids: list[str],
    prev_masses: dict[str, float],
    start_mass: float,
) -> tuple[np.ndarray, dict[str, float]]:
    """Compute per-bot rewards from consecutive states.

    Returns: (rewards array, updated masses dict)
    """
    rewards = np.zeros(len(bot_ids), dtype=np.float32)
    players_by_id = {p["id"]: p for p in curr_state["players"]}
    new_masses = {}

    for i, bot_id in enumerate(bot_ids):
        player = players_by_id.get(bot_id)
        if player is None or not player["isAlive"]:
            # Dead/missing: mass went to 0, so reward = (0 - prev_mass) / start_mass
            prev_mass = prev_masses.get(bot_id, start_mass)
            rewards[i] = -prev_mass / start_mass
            new_masses[bot_id] = start_mass
            continue

        curr_mass = player["mass"]
        prev_mass = prev_masses.get(bot_id, start_mass)
        rewards[i] = (curr_mass - prev_mass) / start_mass
        new_masses[bot_id] = curr_mass

    return rewards, new_masses
