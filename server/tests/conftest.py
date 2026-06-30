"""Put server/ on sys.path so `import app...` works, and provide store/client fixtures."""
import os
import sys

import pytest

sys.path.insert(0, os.path.dirname(os.path.dirname(os.path.abspath(__file__))))

from fastapi.testclient import TestClient  # noqa: E402

from app.main import create_app  # noqa: E402
from app.store import Store  # noqa: E402


@pytest.fixture
def store(tmp_path):
    s = Store(str(tmp_path / "db.sqlite"), str(tmp_path / "assets"))
    yield s
    s.close()


@pytest.fixture
def client(store, tmp_path):
    app = create_app(store, default_export_dir=str(tmp_path / "export"))
    return TestClient(app)
