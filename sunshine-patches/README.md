# Sunshine patches used by Moonlight Hub

`sunshine-2025.924-moonlight-hub.patch` is the full `git diff` of the customised Sunshine tree
(base: LizardByte Sunshine v2025.924, commit 86188d4) that Moonlight Hub runs against:

* `windows_client_pre_rotation` — host-side 90° pre-rotation so a portrait tablet (1200×2000) can
  stream a landscape 2000×1200 virtual display without client-side rotation latency.
* independent touch / desktop touch routing (`src/platform/windows/input.cpp`, `src/input.cpp`).
* **Moonlight Hub log lines** (`src/nvhttp.cpp`) — the only part the Hub strictly needs:
  * `Pairing request from [ip] uniqueid [id] awaiting PIN`
  * `Pairing completed for client [name] uniqueid [id]` / `Pairing failed for uniqueid [id]`
  * `Client requested mode [WxHxFPS] sops [0/1] uniqueid [id]`

Without the patch the Hub still works, but the automatic PIN prompt and the "match the client's
requested resolution" feature fall back to manual pairing / the profile resolution.

Apply on a clean v2025.924 checkout with `git apply sunshine-2025.924-moonlight-hub.patch`.
