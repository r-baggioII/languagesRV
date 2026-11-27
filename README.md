# Language Immersion

Language Immersion es un prototipo educativo para practicar inglés oral en una situación cotidiana. En una cafetería virtual, el estudiante consulta el menú, conversa con Remy, personaliza su pedido y completa un pago simulado. La actividad permite practicar expresiones, comprensión oral y desenvolvimiento en una conversación abierta.

Unity ejecuta la experiencia en Meta Quest. Un backend Python en la red local empareja el visor y conecta el cliente con Gemini Live para transcribir la voz y con Gemini para generar respuestas según el pedido y el contexto. Remy responde con audio sintetizado por Kokoro en el equipo local o por Cartesia a través de su API.

## Requisitos

- Git LFS para descargar las texturas de Small Cafe.
- Unity Hub con Unity `6000.3.23f1`. Para Quest, instalá también Android Build Support, Android SDK & NDK Tools y OpenJDK.
- Un equipo Linux con Python 3.12 para ejecutar ambos servicios. En Debian o Ubuntu, instalá `python3-venv`, `python3-pip`, `espeak-ng` y `libsndfile1`.
- Una clave de Gemini para diálogo y transcripción. Cartesia requiere su propia clave si se elige como proveedor de voz. Kokoro se ejecuta localmente y descarga el modelo en el primer arranque.

## Descargar y abrir el cliente

Con Git LFS instalado, ejecutá estos comandos:

```bash
git lfs install
git clone https://github.com/r-baggioII/languagesRV.git
cd languagesRV
git lfs pull
```

En Unity Hub, agregá esa carpeta como proyecto y abrí `Assets/_Project/Scenes/Cafe_VR.unity`. El simulador XR de la escena permite probar la interacción en el Editor. Para Quest usá el perfil de Android con la lista de escenas del proyecto y la configuración de OpenXR incluida.

## Iniciar los servicios

Desde la raíz del repositorio, prepará el backend y guardá tus credenciales en el archivo local `.env`.

```bash
cd backend/language_immersion_backend
./setup.sh
cp -n .env.example .env
chmod 600 .env
```

Completá `GEMINI_API_KEY` en `.env` y, si vas a usar Cartesia, `CARTESIA_API_KEY`. Para el emparejamiento por PIN, dejá `LAN_ACCESS_TOKEN` vacío. Ejecutá `./run.sh` y consultá el PIN de seis dígitos que aparece en la terminal. El backend escucha en el puerto `8081` con un solo proceso para conservar las sesiones en memoria.

Kokoro es el proveedor de voz predeterminado. Si lo usás, abrí otra terminal desde la raíz del repositorio.

```bash
cd backend/kokoro_lan_server
./setup.sh
./run.sh
```

El primer inicio de Kokoro descarga el modelo y las voces. En el equipo que ejecuta los servicios, `curl http://127.0.0.1:8081/health` comprueba el backend y `curl http://127.0.0.1:8000/health` comprueba Kokoro.

## Emparejar y probar

1. Conectá el visor y el equipo que ejecuta los servicios a la misma red privada.
2. En la configuración de la cafetería, ingresá `http://<IP-del-equipo>:8081` y el PIN de la terminal. Seleccioná **CONNECT / PAIR**. El PIN dura diez minutos y se usa una sola vez.
3. Elegí **Kokoro** o **Cartesia**. Kokoro se conecta al puerto `8000` del mismo equipo.
4. Apuntá a Remy o al mostrador y usá Grip para iniciar el pedido. Cuando te toque responder, mantené presionado el gatillo izquierdo mientras hablás y soltalo para enviar el turno.

En la configuración LAN por HTTP, el PIN, los tokens y el audio viajan sin cifrar. Usá los servicios en una red privada de confianza.

## Pruebas del backend

Desde `backend/language_immersion_backend`, con el entorno creado por `setup.sh`:

```bash
.venv/bin/python -m pip install pytest
.venv/bin/python -m pytest -q
```

Estas pruebas cubren el emparejamiento y las solicitudes a proveedores simulados. La disponibilidad de los modelos y el uso del micrófono y de los mandos se comprueban en una sesión en el visor.

## Créditos y licencias

| Recurso | Licencia o condiciones |
| --- | --- |
| Small Cafe (dylanheyes), cash_register (OTISRAWTDESIGNS), Payment terminal (nofurion), Credit Cards (Zachery_Singh) y One Dollar Bill (kikzz321) | [CC BY 4.0](https://creativecommons.org/licenses/by/4.0/). Las fichas originales y los cambios de integración figuran en los avisos. |
| Unity Editor | [Términos de Unity Editor](https://unity.com/legal/editor-terms-of-service/software). El editor se instala por separado. |
| Universal Render Pipeline, OpenXR Plugin de Unity, XR Interaction Toolkit y sus muestras, uGUI y TextMesh Pro | [Unity Companion License](https://unity.com/legal/licenses/unity-companion-license) para proyectos que usan Unity. Cada paquete conserva sus avisos de terceros. |
| Liberation Sans | [SIL Open Font License 1.1](Assets/StreamingAssets/LIBERATION_SANS_OFL.txt). |
| Kokoro-82M, Kokoro y Misaki | Apache 2.0. Los pesos se descargan en el primer arranque. [Modelo Kokoro-82M](https://huggingface.co/hexgrad/Kokoro-82M), [Kokoro](https://github.com/hexgrad/kokoro), [Misaki](https://github.com/hexgrad/misaki). |
| Dependencias de Python | FastAPI y pytest usan MIT. Uvicorn, HTTPX, python-dotenv, websockets y SoundFile usan BSD-3-Clause. Las distribuciones de PyTorch y NumPy incluyen sus propios avisos. phonemizer-fork y eSpeak NG usan GPL-3.0-o-posterior. Se instalan por separado y conservan sus licencias. |
| Gemini y Cartesia | Servicios externos sujetos a los [términos de Gemini API](https://ai.google.dev/gemini-api/terms) y los [términos de Cartesia](https://www.cartesia.ai/legal/terms). |
| MCP for Unity | Herramienta de desarrollo opcional bajo [licencia MIT](https://github.com/CoplayDev/unity-mcp). |

La distribución usa geometría propia para Remy y el guante de pago. El prototipo anterior usó Coffee Shop Environment de Leartes Studios bajo la [licencia estándar de Unity Asset Store](https://unity.com/legal/as-terms), un personaje y una animación de [Adobe Mixamo](https://helpx.adobe.com/creative-cloud/faq/mixamo-faq.html), y Rigged Hand de Lorenzo Drago bajo la [licencia estándar de Sketchfab](https://sketchfab.com/licenses) con marca NoAI. Esos archivos fuente quedan fuera del repositorio.

Los [avisos de terceros](Assets/StreamingAssets/THIRD_PARTY_NOTICES.txt) acompañan al proyecto y se incluyen en el build mediante `StreamingAssets`. Cada licencia se aplica a su componente. El código y las escenas propias del proyecto carecen de una licencia general de reutilización publicada.
