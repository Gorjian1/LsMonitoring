"""Abstract DataSource + concrete CsvScraperSource (HTTP scrape of CMT-Edge).

ModbusSource is a stub — register map not yet supplied by Worldsensing.

CMT-Edge notes (confirmed on live gateway 169.254.0.1):
- Serves HTTPS on port 443 with a self-signed cert (verify=False needed)
- HTTP requests are redirected to HTTPS
- Current readings: GET /dataserver/current/reading/<node_id>
  returns CSV with Content-Type: application/octet-stream
- Node list: GET /dataserver/ lists networks, each network page
  /dataserver/network/view/<net_id> lists node IDs
- Dead/inactive nodes return 404 or 500 from the current/reading endpoint
"""
from __future__ import annotations

import logging
import re
import urllib3
from abc import ABC, abstractmethod
from dataclasses import dataclass
from typing import Optional

import requests
from requests.auth import HTTPBasicAuth

from .parser import ParsedCsv, parse_csv

urllib3.disable_warnings(urllib3.exceptions.InsecureRequestWarning)
log = logging.getLogger(__name__)


@dataclass
class NodeInfo:
    node_id: int
    model: Optional[str] = None
    label: Optional[str] = None


class DataSource(ABC):
    @abstractmethod
    def connect(self) -> None: ...

    @abstractmethod
    def disconnect(self) -> None: ...

    @abstractmethod
    def discover_nodes(self) -> list[NodeInfo]: ...

    @abstractmethod
    def fetch_csv(self, node_id: int) -> ParsedCsv: ...


class CsvScraperSource(DataSource):
    """Polls /dataserver/current/reading/<id> via HTTPS Basic Auth.

    The gateway always redirects HTTP → HTTPS and uses a self-signed
    certificate, so we use verify=False throughout.
    """

    def __init__(self, gateway_ip: str, username: str, password: str, timeout: float = 8.0):
        self.gateway_ip = gateway_ip.strip()
        self.auth = HTTPBasicAuth(username, password)
        self.timeout = timeout
        self.session: Optional[requests.Session] = None

    @property
    def base_url(self) -> str:
        ip = self.gateway_ip
        # Normalize: always HTTPS; strip any existing scheme first
        ip = re.sub(r'^https?://', '', ip).rstrip("/")
        return f"https://{ip}"

    def connect(self) -> None:
        self.session = requests.Session()
        self.session.auth = self.auth
        self.session.verify = False

    def disconnect(self) -> None:
        if self.session:
            self.session.close()
            self.session = None

    def _get(self, path: str) -> requests.Response:
        if self.session is None:
            self.connect()
        url = f"{self.base_url}{path}"
        log.debug("GET %s", url)
        r = self.session.get(url, timeout=self.timeout)
        r.raise_for_status()
        return r

    # Primary path confirmed on live CMT-Edge; fallbacks for older firmware.
    _CSV_PATH_CANDIDATES = (
        "/dataserver/current/reading/{nid}",
        "/dataserver/node/{nid}/{nid}-readings-current.csv",
        "/dataserver/node/view/{nid}/{nid}-readings-current.csv",
    )

    def fetch_csv(self, node_id: int) -> ParsedCsv:
        last_err: Optional[Exception] = None
        for tmpl in self._CSV_PATH_CANDIDATES:
            path = tmpl.format(nid=node_id)
            try:
                r = self._get(path)
                # If the response is HTML (gateway error page), skip
                ct = r.headers.get("content-type", "")
                if "html" in ct and "text/html" in ct:
                    raise ValueError(f"Got HTML response (node inactive?): HTTP {r.status_code}")
                return parse_csv(r.content)
            except requests.HTTPError as e:
                last_err = e
                if e.response is not None and e.response.status_code in (401, 403):
                    raise  # auth error — no point trying other paths
                continue
            except Exception as e:
                last_err = e
                continue
        raise RuntimeError(f"Could not fetch CSV for node {node_id}: {last_err}")

    # Patterns to extract node IDs from network HTML pages
    _NODE_LINK_RE = re.compile(r'/dataserver/node/(?:view|edit)/(\d+)', re.IGNORECASE)
    _NET_LINK_RE = re.compile(r'/dataserver/network/view/(\d+)', re.IGNORECASE)
    _MODEL_HINT_RE = re.compile(r'LS-[A-Z0-9]+-[A-Z0-9]+', re.IGNORECASE)

    def discover_nodes(self) -> list[NodeInfo]:
        """Scrape CMT-Edge admin to find all node IDs.

        Strategy:
          1. GET / → find all /dataserver/network/view/<net_id> links
          2. GET each network page → find /dataserver/node/view/<node_id> links
          3. Return only nodes that have live current data (200 from fetch_csv)
        """
        found: dict[int, NodeInfo] = {}
        try:
            root = self._get("/dataserver/").text
        except Exception as e:
            log.warning("discover: root page failed: %s", e)
            return []

        net_ids = set(self._NET_LINK_RE.findall(root))
        log.info("discover: found networks %s", net_ids)

        for net_id in net_ids:
            try:
                html = self._get(f"/dataserver/network/view/{net_id}").text
            except Exception:
                continue
            for m in self._NODE_LINK_RE.finditer(html):
                nid = int(m.group(1))
                if nid not in found:
                    found[nid] = NodeInfo(node_id=nid)
                    ctx = self._slice_around(html, str(nid), 300)
                    mm = self._MODEL_HINT_RE.search(ctx) if ctx else None
                    if mm:
                        found[nid].model = mm.group(0)

        log.info("discover: raw node ids %s", list(found))

        # Filter: keep only nodes with live data (200 OK, parseable CSV)
        live: dict[int, NodeInfo] = {}
        for nid, info in found.items():
            try:
                parsed = self.fetch_csv(nid)
                if parsed.model:
                    info.model = parsed.model  # CSV metadata is authoritative
                live[nid] = info
                log.info("discover: node %s alive (%s)", nid, info.model)
            except Exception as e:
                log.debug("discover: node %s no current data: %s", nid, e)

        return sorted(live.values(), key=lambda n: n.node_id)

    @staticmethod
    def _slice_around(text: str, needle: str, window: int) -> Optional[str]:
        i = text.find(needle)
        if i < 0:
            return None
        return text[max(0, i - window): i + len(needle) + window]


class ModbusSource(DataSource):
    """Skeleton for future Modbus TCP implementation.

    Not implemented — Worldsensing register map not yet provided. Do NOT
    guess registers (per CLAUDE_CODE_BRIEF.md).
    """

    def __init__(self, gateway_ip: str, port: int = 502, unit_ids: Optional[list[int]] = None):
        self.gateway_ip = gateway_ip
        self.port = port
        self.unit_ids = unit_ids or []

    def connect(self) -> None:
        raise NotImplementedError("Modbus source pending: need register map from Worldsensing")

    def disconnect(self) -> None:
        pass

    def discover_nodes(self) -> list[NodeInfo]:
        raise NotImplementedError

    def fetch_csv(self, node_id: int) -> ParsedCsv:
        raise NotImplementedError
