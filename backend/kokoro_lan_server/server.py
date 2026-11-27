"""Servidor LAN mínimo para Language Immersion / Remy's Café.

POST /tts acepta {"text": "...", "voice": "am_fenrir", "speed": 0.92}
Devuelve audio WAV PCM mono, 24 kHz, en el cuerpo HTTP.
GET /health comprueba que el modelo inicializó.
"""

from contextlib import asynccontextmanager
from io import BytesIO
from threading import Lock

import numpy as np
import soundfile as sf
from fastapi import FastAPI, HTTPException
from fastapi.responses import Response
from kokoro import KPipeline
from pydantic import BaseModel, Field

SAMPLE_RATE = 24_000
# Primera prueba: limitar a las voces que necesitamos.
ALLOWED_VOICES = {"am_fenrir", "am_michael", "af_heart"}
model_lock = Lock()


class TtsRequest(BaseModel):
    text: str = Field(min_length=1, max_length=700)
    voice: str = "am_fenrir"
    speed: float = Field(default=0.92, ge=0.7, le=1.3)


@asynccontextmanager
async def lifespan(app: FastAPI):
    # Descarga y carga el modelo una vez al iniciar el proceso.
    app.state.pipeline = KPipeline(lang_code="a")
    yield


app = FastAPI(title="Remy's Café — Kokoro TTS", lifespan=lifespan)


@app.get("/health")
def health():
    return {"status": "ok", "service": "kokoro", "sample_rate": SAMPLE_RATE}


@app.post("/tts")
def generate_tts(payload: TtsRequest):
    text = payload.text.strip()
    if not text:
        raise HTTPException(status_code=422, detail="El texto no puede estar vacío")
    if payload.voice not in ALLOWED_VOICES:
        raise HTTPException(status_code=422, detail="Voz no habilitada")

    try:
        # Serialización conservadora: primera prueba con un solo pedido a la vez.
        with model_lock:
            parts = []
            for result in app.state.pipeline(
                text, voice=payload.voice, speed=payload.speed
            ):
                if result.audio is not None:
                    parts.append(result.audio.detach().cpu().numpy())

        if not parts:
            raise HTTPException(status_code=500, detail="Kokoro no generó audio")

        audio = np.concatenate(parts)
        wav_buffer = BytesIO()
        sf.write(wav_buffer, audio, SAMPLE_RATE, format="WAV", subtype="PCM_16")
        return Response(content=wav_buffer.getvalue(), media_type="audio/wav")

    except HTTPException:
        raise
    except Exception as exc:
        # No devolver detalles internos o rutas de archivos al cliente.
        print(f"[Kokoro] Error de síntesis: {type(exc).__name__}: {exc}")
        raise HTTPException(status_code=500, detail="Falló la síntesis de voz") from exc
