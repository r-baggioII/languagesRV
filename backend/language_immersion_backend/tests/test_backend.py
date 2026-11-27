"""Pruebas aisladas; no llaman servicios externos ni consumen créditos."""
import os
import sys
from pathlib import Path

os.environ['GEMINI_API_KEY'] = 'TEST_GEMINI_KEY_DO_NOT_USE'
os.environ['CARTESIA_API_KEY'] = 'TEST_CARTESIA_KEY_DO_NOT_USE'
os.environ['LAN_ACCESS_TOKEN'] = 'local-test-token-12345678901234567890'
sys.path.insert(0, str(Path(__file__).resolve().parents[1]))

import httpx
from fastapi.testclient import TestClient
import server


class FakeOutbound:
    def __init__(self):
        self.requests = []

    async def post(self, url, json=None, headers=None):
        self.requests.append((url, json, headers))
        if 'cartesia.ai' in url or '/tts' in url:
            return httpx.Response(200, content=b'RIFF-SAMPLE', headers={'Content-Type': 'audio/wav'})
        return httpx.Response(200, json={'id': 'interaction-abc', 'steps': []})

    async def get(self, url, headers=None):
        self.requests.append((url, None, headers))
        return httpx.Response(200, json={'id': 'interaction-abc'})


def test_health_and_auth():
    with TestClient(server.app) as client:
        assert client.get('/health').status_code == 200
        assert client.post('/api/gemini/interactions', json={}).status_code == 401


def test_unity_pairing_exchanges_one_time_pin_for_session(monkeypatch):
    pins = iter((654321, 654322))
    monkeypatch.setattr(server.secrets, 'randbelow', lambda _limit: next(pins))
    with TestClient(server.app) as client:
        assert client.get('/api/health').status_code == 401
        assert client.post('/api/pair', json={'pin': '000000'}).status_code == 401
        response = client.post('/api/pair', json={'pin': '654321'})
        assert response.status_code == 200
        session = response.json()
        assert session['expires_in'] == server.SESSION_TTL_SECONDS
        assert session['token'] != '654321'
        assert client.post('/api/pair', json={'pin': '654321'}).status_code == 401
        assert client.get('/api/health', headers={'X-Language-Token': '654321'}).status_code == 401
        assert client.get('/api/health', headers={'X-Language-Token': session['token']}).status_code == 200


def test_gemini_and_cartesia_proxy_no_key_sent_by_client():
    fake = FakeOutbound()
    header = {'X-Language-Token': os.environ['LAN_ACCESS_TOKEN']}
    with TestClient(server.app) as client:
        client.app.state.http = fake
        response = client.post('/api/gemini/interactions', headers=header, json={
            'model': 'gemini-3.8-flash', 'input': 'Hi!'
        })
        assert response.status_code == 200
        assert response.json()['id'] == 'interaction-abc'
        assert fake.requests[0][0] == 'https://generativelanguage.googleapis.com/v1beta/interactions'
        assert fake.requests[0][2]['x-goog-api-key'] == os.environ['GEMINI_API_KEY']
        assert os.environ['GEMINI_API_KEY'] not in response.text
        tts = client.post('/api/cartesia/tts/bytes', headers=header, json={
            'model_id': 'sonic-3.6',
            'transcript': 'Hello',
            'voice': 'db6b0ed5-d5d3-463d-ae85-518a07d3c2b4',
            'output_format': {'container': 'wav', 'encoding': 'pcm_s16le', 'sample_rate': 44100}
        })
        assert tts.status_code == 200
        assert tts.content == b'RIFF-SAMPLE'
        assert fake.requests[-1][2]['Authorization'] == f"Bearer {os.environ['CARTESIA_API_KEY']}"
        assert fake.requests[-1][2]['Cartesia-Version'] == '2026-08-14'
        kokoro = client.post('/api/kokoro/tts', headers=header, json={
            'text': 'Hello', 'voice': 'am_fenrir', 'speed': 0.92
        })
        assert kokoro.status_code == 200
        assert fake.requests[-1][0] == 'http://127.0.0.1:8000/tts'
        generation = client.post('/api/gemini/models/gemini-3.8-flash/generateContent', headers=header, json={
            'contents': [{'parts': [{'text': 'Hi!'}]}]
        })
        assert generation.status_code == 200
        assert ':generateContent' in fake.requests[-1][0]


def test_untrusted_path_and_invalid_payload():
    header = {'X-Language-Token': os.environ['LAN_ACCESS_TOKEN']}
    with TestClient(server.app) as client:
        assert client.post('/api/gemini/models/x!bad/generateContent', headers=header, json={}).status_code == 422
        assert client.post('/api/cartesia/tts/bytes', headers=header, json={'transcript': 'a' * 701}).status_code == 422
        assert client.post('/api/gemini/interactions', headers=header, content='not json').status_code == 400
