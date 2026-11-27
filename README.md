# Language Immersion

Language Immersion is an educational prototype for practicing spoken English in an everyday setting. In a virtual café, learners read the menu, talk to Remy, customize their order, and complete a simulated payment. The activity gives learners a chance to practice common expressions, listening comprehension, and the ability to respond in an open-ended conversation.

The Unity client runs on Meta Quest. A Python backend on the local network pairs the headset and connects the client to Gemini Live for speech transcription and to Gemini for replies that account for the order and conversation context. Kokoro generates Remy's voice on the local computer, and Cartesia is available as a cloud alternative.

## Requirements

- Git LFS to download the Small Cafe textures.
- Unity Hub with Unity `6000.3.23f1`. For Quest builds, also install Android Build Support, Android SDK & NDK Tools, and OpenJDK.
- A Linux computer with Python 3.12 to run the services. On Debian or Ubuntu, install `python3-venv`, `python3-pip`, `espeak-ng`, and `libsndfile1`.
- A Gemini API key for dialogue and transcription. If you choose Cartesia for text-to-speech, you also need a Cartesia API key. Kokoro runs locally and downloads its model the first time it starts.

## Download and open the Unity project

With Git LFS installed, run the following commands.

```bash
git lfs install
git clone https://github.com/r-baggioII/languagesRV.git
cd languagesRV
git lfs pull
```

In Unity Hub, add the repository folder as a project and open `Assets/_Project/Scenes/Cafe_VR.unity`. Use the scene's XR Interaction Simulator to test interactions in the Unity Editor. For Quest, select the Android build profile and use the project scene list and the included OpenXR settings.

## Start the services

From the repository root, set up the backend and store your credentials in a local `.env` file.

```bash
cd backend/language_immersion_backend
./setup.sh
cp -n .env.example .env
chmod 600 .env
```

Set `GEMINI_API_KEY` in `.env`. If you plan to use Cartesia, also set `CARTESIA_API_KEY`. Leave `LAN_ACCESS_TOKEN` empty to pair with a PIN. Run `./run.sh` and look for the six-digit PIN in the terminal. The backend listens on port `8081` and uses a single worker to keep sessions in memory.

Kokoro is the default text-to-speech provider. To use it, open a second terminal at the repository root.

```bash
cd backend/kokoro_lan_server
./setup.sh
./run.sh
```

Kokoro downloads the model and voices the first time it starts. On the computer running the services, use `curl http://127.0.0.1:8081/health` to check the backend and `curl http://127.0.0.1:8000/health` to check Kokoro.

## Pair the headset and try the café

1. Connect the headset and the computer running the services to the same private network.
2. In the café settings, enter `http://<computer-LAN-IP>:8081` and the PIN from the terminal. Select **CONNECT / PAIR**. The PIN expires after ten minutes and can be used once.
3. Choose **Kokoro** or **Cartesia**. With Kokoro selected, the headset connects to port `8000` on the same computer.
4. Point at Remy or the counter and press the Grip button to start an order. When it is your turn to speak, hold the left trigger while you talk and release it to send your turn.

With the LAN configured for HTTP, the PIN, session tokens, and audio travel unencrypted. Run the services on a trusted private network.

## Backend tests

From `backend/language_immersion_backend`, use the environment created by `setup.sh`.

```bash
.venv/bin/python -m pip install pytest
.venv/bin/python -m pytest -q
```

These tests cover pairing and requests to simulated providers. Use a live headset session to verify model availability, the microphone, and the controllers.

## Credits and licenses

| Resource | License or terms |
| --- | --- |
| Small Cafe (dylanheyes), cash_register (OTISRAWTDESIGNS), Payment terminal (nofurion), Credit Cards (Zachery_Singh), and One Dollar Bill (kikzz321) | [CC BY 4.0](https://creativecommons.org/licenses/by/4.0/). The third-party notices list the original sources and describe the changes made for this project. |
| Unity Editor | [Unity Editor Software Terms](https://unity.com/legal/editor-terms-of-service/software). Install the editor separately. |
| Universal Render Pipeline, Unity OpenXR Plugin, XR Interaction Toolkit and its samples, uGUI, and TextMesh Pro | [Unity Companion License](https://unity.com/legal/licenses/unity-companion-license) for projects that use Unity. Each package includes its own third-party notices. |
| Liberation Sans | [SIL Open Font License 1.1](Assets/StreamingAssets/LIBERATION_SANS_OFL.txt). |
| Kokoro-82M, Kokoro, and Misaki | Apache 2.0. The model weights are downloaded on first launch. [Kokoro-82M model](https://huggingface.co/hexgrad/Kokoro-82M), [Kokoro](https://github.com/hexgrad/kokoro), [Misaki](https://github.com/hexgrad/misaki). |
| Python dependencies | FastAPI and pytest use MIT. Uvicorn, HTTPX, python-dotenv, websockets, and SoundFile use BSD-3-Clause. PyTorch and NumPy distributions include their own notices. phonemizer-fork and eSpeak NG use GPL-3.0-or-later. These dependencies are installed separately under their respective licenses. |
| Gemini and Cartesia | External services governed by the [Gemini API terms](https://ai.google.dev/gemini-api/terms) and the [Cartesia terms](https://www.cartesia.ai/legal/terms). |

This release uses original geometry for Remy and the payment glove. The earlier prototype used Leartes Studios' Coffee Shop Environment under the [standard Unity Asset Store EULA](https://unity.com/legal/as-terms), a character and animation from [Adobe Mixamo](https://helpx.adobe.com/creative-cloud/faq/mixamo-faq.html), and Lorenzo Drago's Rigged Hand under the [Sketchfab Standard License](https://sketchfab.com/licenses) with the NoAI tag. Those source files were excluded from this release.

The [third-party notices](Assets/StreamingAssets/THIRD_PARTY_NOTICES.txt) accompany the project and are included in builds through `StreamingAssets`. Each listed license applies to its own component. The project's original code and scenes have no published general-purpose license for reuse.
