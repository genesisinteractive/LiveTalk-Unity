"""Load the JSON ``build.py`` writes (``LP_DRIVER_CONFIG``). Empty if unset."""
import json
import os


def load():
    path = os.environ.get("LP_DRIVER_CONFIG")
    if not path or not os.path.isfile(path):
        return {}
    with open(path, encoding="utf-8") as f:
        return json.load(f)
