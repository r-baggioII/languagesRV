"""Language Immersion: backend LAN para Gemini, Cartesia y Kokoro.

No colocar claves de proveedores en Unity. Este servidor debe usarse solo en LAN confiable.
"""
import asyncio
import hashlib
import hmac
import os
import re
import secrets
import threading
import time
from contextlib import asynccontextmanager
from pathlib import Path
from urllib.parse import urlencode

import httpx
from dotenv import load_dotenv
from fastapi import Depends, FastAPI, Header, HTTPException, Request, WebSocket
from fastapi.responses import Response
from starlette.websockets import WebSocketDisconnect
from websockets.asyncio.client import connect
from websockets.exceptions import ConnectionClosed

load_dotenv(Path(__file__).resolve().parent / ".env")

GEMINI_API_KEY = os.getenv("GEMINI_API_KEY", "").strip()
CARTESIA_API_KEY = os.getenv("CARTESIA_API_KEY", "").strip()
LAN_ACCESS_TOKEN = os.getenv("LAN_ACCESS_TOKEN", "").strip()
CARTESIA_VERSION = os.getenv("CARTESIA_VERSION", "2026-08-14").strip()
KOKORO_URL = os.getenv("KOKORO_URL", "http://127.0.0.1:8000").rstrip("/")

GEMINI_API_ROOT = "https://generativelanguage.googleapis.com/v1beta"
CARTESIA_TTS_BYTES = "https://api.cartesia.ai/tts/bytes"
GEMINI_LIVE_WS = (
    "wss://generativelanguage.googleapis.com/ws/"
    "google.ai.generativelanguage.v1beta.GenerativeService.BidiGenerateContent"
)
MODEL_PATTERN = re.compile(r"^[a-zA-Z0-9._-]{1,100}$")
INTERACTION_ID_PATTERN = re.compile(r"^[a-zA-Z0-9._-]{1,300}$")


# Configuración del emparejamiento: PIN efímero, credencial larga en memoria.
PIN_TTL_SECONDS = 10 * 60
SESSION_TTL_SECONDS = 8 * 60 * 60
MAX_FAILED_PAIRINGS = 5
PAIR_LOCK_SECONDS = 5 * 60
MAX_SESSIONS = 16


class PairingManager:
    """Un solo proceso/worker. No persistir PIN ni sesiones en archivos."""

    def __init__(self):
        self._lock = threading.Lock()
        self._pin = None
        self._pin_deadline = 0.0
        self._failed = 0
        self._locked_until = 0.0
        # Solo se almacenan resúmenes SHA-256 de los tokens de sesión.
        self._sessions = {}

    def _rotate_locked(self):
        self._pin = f"{secrets.randbelow(1_000_000):06d}"
        self._pin_deadline = time.monotonic() + PIN_TTL_SECONDS
        self._failed = 0
        # Este PIN aparece SOLO en la consola de la notebook, no vía HTTP.
        print(
            "\n[Language Immersion] PIN TEMPORAL PARA EL CASCO: "
            f"{self._pin} (10 minutos, un uso).\n"
            "[Language Immersion] No mostrar esta terminal a terceros.\n",
            flush=True,
        )

    def start(self):
        with self._lock:
            self._sessions.clear()
            self._locked_until = 0.0
            self._rotate_locked()

    def exchange(self, pin: str) -> tuple[str, int]:
        now = time.monotonic()
        with self._lock:
            if now < self._locked_until:
                raise HTTPException(status_code=429, detail="Emparejamiento bloqueado temporalmente")
            if self._pin is None or now >= self._pin_deadline:
                self._rotate_locked()
                raise HTTPException(status_code=410, detail="PIN vencido. Consultar nuevo PIN en la notebook")
            if not hmac.compare_digest(pin, self._pin):
                self._failed += 1
                if self._failed >= MAX_FAILED_PAIRINGS:
                    self._locked_until = now + PAIR_LOCK_SECONDS
                    # Bloqueo global: evita probar sistemáticamente los 6 dígitos.
                    raise HTTPException(status_code=429, detail="Demasiados intentos. Esperar cinco minutos")
                raise HTTPException(status_code=401, detail="PIN incorrecto")

            # Rechazar más de un número controlado de sesiones concurrentes.
            self._sessions = {k: t for k, t in self._sessions.items() if t > now}
            if len(self._sessions) >= MAX_SESSIONS:
                raise HTTPException(status_code=503, detail="Demasiadas sesiones activas")
            token = secrets.token_urlsafe(32)  # 256 bits criptográficamente aleatorios
            digest = hashlib.sha256(token.encode("ascii")).digest()
            self._sessions[digest] = now + SESSION_TTL_SECONDS
            self._rotate_locked()  # El PIN utilizado se invalida de inmediato.
            return token, SESSION_TTL_SECONDS

    def is_valid(self, token: str | None) -> bool:
        if not token:
            return False
        # Compatibilidad temporal con el token largo anterior para el Editor.
        if LAN_ACCESS_TOKEN and hmac.compare_digest(token, LAN_ACCESS_TOKEN):
            return True
        digest = hashlib.sha256(token.encode("utf-8")).digest()
        now = time.monotonic()
        with self._lock:
            deadline = self._sessions.get(digest)
            if deadline is None:
                return False
            if deadline <= now:
                del self._sessions[digest]
                return False
            return True


pairing = PairingManager()


@asynccontextmanager
async def lifespan(app: FastAPI):
    if LAN_ACCESS_TOKEN and len(LAN_ACCESS_TOKEN) < 20:
        raise RuntimeError("LAN_ACCESS_TOKEN debe tener al menos 20 caracteres o estar vacío")
    pairing.start()
    async with httpx.AsyncClient(timeout=httpx.Timeout(90.0, connect=12.0)) as client:
        app.state.http = client
        yield


app = FastAPI(
    title="Language Immersion — Backend LAN",
    description="Pasarela local para Gemini, Cartesia y Kokoro; no usar en Internet público.",
    lifespan=lifespan,
)


def require_token(x_language_token: str | None = Header(default=None)) -> None:
    if not pairing.is_valid(x_language_token):
        raise HTTPException(status_code=401, detail="Acceso no autorizado")


async def json_body(request: Request, max_bytes: int) -> dict:
    content_length = request.headers.get("content-length")
    if content_length:
        try:
            if int(content_length) > max_bytes:
                raise HTTPException(status_code=413, detail="Solicitud demasiado grande")
        except ValueError:
            raise HTTPException(status_code=400, detail="Content-Length inválido")
    body = await request.body()
    if len(body) > max_bytes:
        raise HTTPException(status_code=413, detail="Solicitud demasiado grande")
    try:
        import json
        value = json.loads(body)
    except (ValueError, UnicodeDecodeError):
        raise HTTPException(status_code=400, detail="Se requiere JSON válido")
    if not isinstance(value, dict):
        raise HTTPException(status_code=422, detail="El cuerpo debe ser un objeto JSON")
    return value


async def proxy_json_request(request: Request, upstream_url: str, provider_headers: dict, max_bytes: int = 1_000_000):
    payload = await json_body(request, max_bytes)
    try:
        upstream = await request.app.state.http.post(
            upstream_url, json=payload, headers=provider_headers
        )
    except httpx.TimeoutException:
        raise HTTPException(status_code=504, detail="Timeout del proveedor")
    except httpx.RequestError:
        raise HTTPException(status_code=502, detail="Proveedor no disponible")
    # Preservar el cuerpo exacto (incluidas respuestas con error), de modo que
    # el cliente Unity pueda mantener su parsing actual.
    return Response(
        content=upstream.content,
        status_code=upstream.status_code,
        media_type=upstream.headers.get("content-type", "application/json").split(";")[0],
    )


@app.get("/health")
def health():
    # No muestra claves, valores de tokens ni detalles internos del servidor.
    return {"status": "ok", "service": "language-immersion-backend"}


@app.post("/api/pair")
async def pair_headset(request: Request):
    """Un PIN NO autoriza llamadas a Gemini: entrega token largo temporal."""
    payload = await json_body(request, 256)
    pin = payload.get("pin")
    if not isinstance(pin, str) or not re.fullmatch(r"[0-9]{6}", pin):
        raise HTTPException(status_code=422, detail="Ingresá un PIN de seis dígitos")
    token, ttl = pairing.exchange(pin)
    return {"token": token, "expires_in": ttl}


@app.get("/api/health", dependencies=[Depends(require_token)])
def authenticated_health():
    return {"status": "ok", "service": "language-immersion-backend"}


@app.post("/api/gemini/interactions", dependencies=[Depends(require_token)])
async def gemini_interactions(request: Request):
    if not GEMINI_API_KEY:
        raise HTTPException(status_code=503, detail="Gemini no configurado")
    return await proxy_json_request(
        request,
        f"{GEMINI_API_ROOT}/interactions",
        {"x-goog-api-key": GEMINI_API_KEY, "Content-Type": "application/json"},
    )


@app.get("/api/gemini/interactions/{interaction_id}", dependencies=[Depends(require_token)])
async def gemini_get_interaction(interaction_id: str, request: Request):
    if not GEMINI_API_KEY:
        raise HTTPException(status_code=503, detail="Gemini no configurado")
    if not INTERACTION_ID_PATTERN.fullmatch(interaction_id):
        raise HTTPException(status_code=422, detail="Interaction ID inválido")
    try:
        upstream = await request.app.state.http.get(
            f"{GEMINI_API_ROOT}/interactions/{interaction_id}",
            headers={"x-goog-api-key": GEMINI_API_KEY},
        )
    except httpx.TimeoutException:
        raise HTTPException(status_code=504, detail="Timeout del proveedor")
    except httpx.RequestError:
        raise HTTPException(status_code=502, detail="Proveedor no disponible")
    return Response(
        content=upstream.content, status_code=upstream.status_code,
        media_type=upstream.headers.get("content-type", "application/json").split(";")[0]
    )


@app.post("/api/gemini/models/{model}/generateContent", dependencies=[Depends(require_token)])
async def gemini_generate_content(model: str, request: Request):
    """Para clientes STT que usen REST generateContent, no para streaming."""
    if not GEMINI_API_KEY:
        raise HTTPException(status_code=503, detail="Gemini no configurado")
    if not MODEL_PATTERN.fullmatch(model):
        raise HTTPException(status_code=422, detail="Modelo inválido")
    return await proxy_json_request(
        request,
        f"{GEMINI_API_ROOT}/models/{model}:generateContent",
        {"x-goog-api-key": GEMINI_API_KEY, "Content-Type": "application/json"},
        max_bytes=12_000_000,  # audio embebido en base64 en una solicitud REST
    )


@app.post("/api/cartesia/tts/bytes", dependencies=[Depends(require_token)])
async def cartesia_tts_bytes(request: Request):
    if not CARTESIA_API_KEY:
        raise HTTPException(status_code=503, detail="Cartesia no configurado")
    data = await json_body(request, 50_000)
    transcript = data.get("transcript")
    if not isinstance(transcript, str) or not transcript.strip() or len(transcript) > 700:
        raise HTTPException(status_code=422, detail="transcript requerido (máx. 700 caracteres)")
    try:
        upstream = await request.app.state.http.post(
            CARTESIA_TTS_BYTES,
            json=data,
            headers={
                "Authorization": f"Bearer {CARTESIA_API_KEY}",
                "Cartesia-Version": CARTESIA_VERSION,
                "Content-Type": "application/json",
            },
        )
    except httpx.TimeoutException:
        raise HTTPException(status_code=504, detail="Timeout del proveedor")
    except httpx.RequestError:
        raise HTTPException(status_code=502, detail="Proveedor no disponible")
    return Response(
        content=upstream.content,
        status_code=upstream.status_code,
        media_type=upstream.headers.get("content-type", "application/octet-stream").split(";")[0],
    )


@app.post("/api/kokoro/tts", dependencies=[Depends(require_token)])
async def kokoro_tts(request: Request):
    """Opcional: puerta de entrada única. Kokoro sigue en el puerto 8000."""
    data = await json_body(request, 3_000)
    try:
        upstream = await request.app.state.http.post(
            f"{KOKORO_URL}/tts", json=data
        )
    except httpx.TimeoutException:
        raise HTTPException(status_code=504, detail="Kokoro no respondió a tiempo")
    except httpx.RequestError:
        raise HTTPException(status_code=502, detail="No se pudo conectar con Kokoro")
    return Response(
        content=upstream.content,
        status_code=upstream.status_code,
        media_type=upstream.headers.get("content-type", "application/octet-stream").split(";")[0],
    )


@app.websocket("/api/gemini/live")
async def gemini_live(websocket: WebSocket):
    """Relay binario/texto para la API Gemini Live basada en WebSocket.

    Solo útil si el cliente Unity usa el protocolo de Gemini Live. No convierte
    automáticamente otro mecanismo de transcripción en Gemini Live.
    """
    token = websocket.headers.get("x-language-token", "")
    if not GEMINI_API_KEY or not pairing.is_valid(token):
        await websocket.close(code=1008)
        return

    # Gemini documenta el parámetro key= para su conexión WebSocket.
    # La URL es fija salvo la clave privada y no se registra en los logs.
    upstream_url = f"{GEMINI_LIVE_WS}?{urlencode({'key': GEMINI_API_KEY})}"
    try:
        async with connect(upstream_url, max_size=32 * 1024 * 1024, open_timeout=12) as upstream:
            await websocket.accept()

            async def client_to_gemini():
                while True:
                    frame = await websocket.receive()
                    if frame["type"] == "websocket.disconnect":
                        break
                    if frame.get("text") is not None:
                        await upstream.send(frame["text"])
                    elif frame.get("bytes") is not None:
                        await upstream.send(frame["bytes"])

            async def gemini_to_client():
                async for frame in upstream:
                    if isinstance(frame, str):
                        await websocket.send_text(frame)
                    else:
                        await websocket.send_bytes(frame)

            tasks = [asyncio.create_task(client_to_gemini()), asyncio.create_task(gemini_to_client())]
            done, pending = await asyncio.wait(tasks, return_when=asyncio.FIRST_COMPLETED)
            for task in pending:
                task.cancel()
            await asyncio.gather(*pending, return_exceptions=True)
            for task in done:
                try:
                    task.result()
                except (WebSocketDisconnect, ConnectionClosed):
                    pass
    except (WebSocketDisconnect, ConnectionClosed):
        pass
    except Exception:
        # No registrar el error crudo; puede contener la URL con la API key.
        if websocket.application_state.name == "CONNECTED":
            try:
                await websocket.close(code=1011)
            except RuntimeError:
                pass
    finally:
        if websocket.application_state.name == "CONNECTED":
            try:
                await websocket.close()
            except RuntimeError:
                pass
