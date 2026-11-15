# Prepare genuine local acceptance

The audit uses the production frontend (`http://localhost:3000`), Firebase-authenticated API (`http://localhost:5100`), dedicated SQL LocalDB database and Python pipeline (`http://127.0.0.1:8100`). After the user restarted the updated launcher, all three services and the supplied public API tunnel returned HTTP 200. Initial `/ready` was 503 before model registration; after genuine startup/registration, independent API `/ready` returned **200**, `ready: true`, with database, Colab and pipeline checks ready. Health alone is not model or clinical readiness.

Use only designated test accounts and synthetic/de-identified fixtures. This is not a production deployment or clinical validation. In particular, the outstanding unrestricted CORS and dependency advisories are not resolved by opening a tunnel.

## 0. Use the corrected notebook and source archive

The old Drive notebook combines upload with global-Python package installation. Its cancelled installation is not a success. Do **not** run that old install cell with the replacement ZIP. The corrected local notebook has six separate cells and creates an isolated Python **3.12** environment because the observed Colab host uses Python 3.13, which vLLM 0.8.5 does not support.

Requirements now pin **vLLM 0.8.5 / Torch 2.6.0 / Transformers 4.51.3**. The Python 3.12 installer uses uv 0.9.12, requires binary wheels and checks installed dependencies/GPU availability. The sentence-transformers 2.7.0 reranker constructor is corrected and vLLM request logging is disabled. Local Python regressions pass **383 tests**. The genuine Colab installation and offline-test cells also completed successfully; their checked subprocess output was not visible, so no Colab test count or detailed package-log capture is claimed. Genuine GPU model readiness/registration now passed; clinical behavior remains unverified.

The reviewed ZIP is `D:\clinical-rag-platform\clinical-rag-platform\clinical-rag-colab-source.zip` (**111,723 bytes; 45 source files**). SHA-256: `434d10726c80e0cf5b16327f9e442944c635610205f1eb568dc27e79033b6a09`. Manifest hashes, member exclusions and round-trip extraction were checked. The builder refuses to overwrite existing archives; rebuild to a new filename if source changes. The local notebook is `colab/clinical_rag_colab.ipynb`. Through the user's Chrome extension it was uploaded as a new Drive notebook, preserving the old copy: `https://colab.research.google.com/drive/1gubAKemEfASufmOMnGt-1m9wUv24Upft`. Its T4 runtime is connected; tunnel-binary setup and source upload completed. The installation cell finished and printed **`INSTALL COMPLETE — run the offline-test cell next.`** after its checked installer subprocess returned successfully. Child-process package/version output was not visible in the notebook; no full package-log capture is claimed. A new notebook may need its own Colab Secrets access permissions; the user's screenshot shows the three switches enabled.

## 1. Prepare test identities

1. Prepare **two distinct email addresses whose inboxes you control**, or use an existing dedicated test owner account plus one new test email. There is no need to create a personal account for the assistant.
2. In Firebase Console, select the project that matches both this backend and `frontend/.env.local` (currently `clinical-rag-690c8`). Under Authentication → Sign-in method, enable **Email/Password**. If you do not control this project, use your own project and update both configurations, then rebuild/restart the frontend and API.
3. Sign in with the user-selected existing accounts, or use the actual `/signup` UI if an account must be created. **You** handle passwords and open any verification email in the controlled inbox. Do not mark users verified through a bypass just to pass the test. Refresh/sign in again afterwards so the bearer token contains the verified claim.
4. Keep user B **uninvited** until workspace isolation is tested. Owner A's workspace must be absent from B's list, and direct access to A's workspace/resource paths must be denied.

Passwords and bearer tokens must not be pasted into chat, source code, terminal arguments or reports. Use secure browser handoff for sign-in/verification. Providing the designated test emails is sufficient to coordinate the next step.

Account A completed secure sign-in and created the real isolated audit workspace `3ad97b32-3ee1-4e0e-aab5-fb783de7ac6b`. A privacy-limited browser check matched the designated email without returning credentials; owner-only members returned its sole owner membership and `/api/operations/colab` returned 200, independently enforcing the verified bearer claim. Account B subsequently completed private sign-in; its designated identity and verified bearer claim were confirmed independently. A remained sole owner with no invitations before switching. Uninvited B received an empty workspace list (200) and **404 on all 12 tested scoped reads**: workspace, members, document list, both documents/progress/chunks, published configuration, rules and tests. A's workspace was absent from the list UI; direct Documents navigation showed an access-failure alert without fixture filenames, source markers or review links. The browser returned to B's `/workspaces`, with no invitation accepted or workspace created. These are existing-account authentication/read-isolation results, not new signup or verification-email-flow coverage. A harmless answer-prompt marker was saved as draft v2 and reloaded exactly; other configuration and published v1 remain unchanged. The draft test button was enabled but created no run; publishing remains disabled with “no test run”.

## 2. Prepare a source fixture

Choose a PDF that can legally be processed in Colab and contains **no patient information**. It should have at least five meaningful pages so we can choose pages **1, 3, 5** and verify that gap pages 2 and 4 are not chunked. Provide its publishing society, year and actual FINAL/DRAFT provenance.

To verify both source statuses, prepare separate truthful FINAL and DRAFT fixtures, or a clearly labelled synthetic DRAFT fixture with a known test quotation. Do not label a final guideline DRAFT and claim that proves real provenance. Provide one question and one fully synthetic case grounded in the fixture; rule triggering needs a known matching fact pattern. These are integration tests, not evidence of clinical correctness.

The user selected synthetic fixtures. `scripts/prepare_acceptance_fixtures.py` generated two five-page PDFs and `acceptance-fixture-manifest.json` under `C:\Users\abdom\AppData\Local\Temp\opencode\final-audit-synthetic-fixtures`. Their FINAL/DRAFT labels refer solely to finalized/unfinished **synthetic test artifacts**, not genuine guideline provenance or clinician approval. PDF hashes, exact quotations, unique page markers and no-overwrite protection were checked; representative rendered pages were visually inspected because native PDF input is unavailable here. The manifest supplies page selection `[1,3,5]`, exclusion markers for pages 2/4, test questions and a wholly fictional case. Both PDFs were uploaded and automatically parsed through the app (3,198 FINAL / 3,222 DRAFT characters). Selected pages produced three verbatim, source-matching chunks each, all accepted individually and genuinely embedded; both are indexed, coverage stays partial/60%, and gap-page markers are absent. The FINAL marker-answer expectation failed despite correct retrieval; a separate known-quote question returned one UI-verified exact source quotation and FINAL citation. The DRAFT question returned four source-matching items across FINAL/DRAFT, with two DRAFT citation badges and all three DRAFT retrieved sources badged. Full fictional case acceptance **failed**: three planned problems, 18 source occurrences from selected pages, but zero verified answers/all Not covered. Inputs are not included in the report. These files are not part of the source ZIP or original evaluation artifacts, and none of these observations certifies clinical correctness.

## 3. Give Colab a public registration route to the local API

Colab cannot reach your computer's `localhost`. It needs a **public HTTPS tunnel to port 5100** for registration and heartbeats. Install Cloudflare's `cloudflared` on Windows if needed:

```powershell
winget install --id Cloudflare.cloudflared --exact
```

Open a new PowerShell terminal if PATH has not refreshed, then run:

```powershell
cloudflared tunnel --url http://127.0.0.1:5100
```

Keep that terminal running. Copy the resulting `https://...trycloudflare.com` **origin** for Colab's `ASPNET_BACKEND_URL`. It must not have a path, query or credentials. This temporarily exposes the test API over HTTPS; use only the isolated audit database and close the tunnel after testing. Avoid proxy/tunnel body or credential inspection/retention.

**Keep the frontend API base and Python retrieval callback on `http://localhost:5100`.** The public tunnel is for Colab registration; changing `Backend__PublicUrl` alone would break the pipeline's exact callback-origin checks.

There will be **two different tunnels**:

- Computer → API port **5100**, started above.
- Colab → model port **8000**, started automatically by the notebook's last cell.

## 4. Configure the registration secret securely

Use your password manager to generate two independent random secrets of at least 32 printable characters without spaces:

- **Registration secret**: used by local API `Colab__RegistrationSecret` and notebook `COLAB_REGISTRATION_SECRET`.
- **Model API key**: notebook `COLAB_API_KEY`; automatically encrypted into the API's database on successful registration.

Do not send either value in chat or put them in `NEXT_PUBLIC_*` variables.

The audit created this local-only launcher under the approved temp directory:

```powershell
& 'C:\Users\abdom\AppData\Local\Temp\opencode\final-audit-start.ps1' -Restart -ConfigureColab
```

Run it **yourself in PowerShell**, not through an assistant tool requiring a plaintext argument. It prompts for the registration secret with hidden input, checks recorded process ownership, reuses the audit database/key-ring directory, and restarts the three services in hidden windows. It generates a fresh matching API/Python service key internally without displaying it. New hidden-input entries are stored as Windows DPAPI ciphertext in the isolated run directory; subsequent `-Restart` runs can reuse that encrypted entry under the same Windows user. Previous entries made before this change were not saved and must be entered once again. Launcher syntax and successful three-service startup were observed; the encrypted file's existence was checked without reading it. DPAPI reuse on another restart has not yet been verified.

The optional platform-admin UID prompt can be left blank: **automatic notebook registration does not require a Firebase administrator**. If you want to test manual connection controls later, use a verified test user's UID from Firebase Console → Authentication → Users. A workspace owner is already sufficient for the requested document, prompt and rule actions, but is not automatically a platform administrator.

The optional UID is **not a secret or model key**. Leave it blank when unused. The launcher now clears a previously entered environment UID when that prompt is skipped and refuses the registration secret as the UID. If any secret has been shared in chat or screenshots, rotate it privately in both the local launcher and Colab Secrets before non-test use; never copy the exposed value into reports or commands.

The launcher/state are temporary local audit tools, not committed deployment scripts. They do not change your normal appsettings database. `Privacy__StoreResults=false` is set, and uploads after this restart use the audit's isolated temp upload directory. Keep the SQL database and key ring in place while testing so saved drafts and protected connection credentials survive restarts.

## 5. Start genuine Colab models

1. Open `colab/clinical_rag_colab.ipynb` in Google Colab.
2. Runtime → Change runtime type → select a **GPU**. A genuine Tesla T4 was observed and the real models became ready. L4/A100 may offer more headroom, but concurrent load or memory headroom is not certified. Do not approve a paid runtime/subscription merely on the assistant's behalf.
3. In the notebook's **Secrets** panel (key icon), add these values and enable notebook access:

   | Colab secret | Value |
   | --- | --- |
   | `COLAB_API_KEY` | Independent model API key from your password manager |
   | `COLAB_REGISTRATION_SECRET` | Exactly the secret entered into the local launcher |
   | `ASPNET_BACKEND_URL` | Computer's public HTTPS API tunnel origin from step 3 |

4. Upload the reviewed **source-only** ZIP from step 0 when cell 2 asks. It contains no `.env*`, appsettings credentials, key rings, uploads, databases, build outputs, dependency directories, caches or patient/evaluation questions. Shared JSON and synthetic fixtures are explicitly selected; `requirements.txt` is at the archive root.
5. Run the six cells **in order** in a fresh GPU runtime: tunnel binary → source upload → validated extraction/Python 3.12 package install → offline tests → real model server → readiness/tunnel/registration. Wait for each cell to stop running and its completion marker. **`UPLOAD COMPLETE` is not `INSTALL COMPLETE`**. Stop after errors/cancellation; never run all cells blindly. Model downloads/initialization may take several minutes.
6. Cell 4 must print **`OFFLINE TESTS PASSED`**. Cell 5 only starts loading models. Cell 6 must print **`COLAB REGISTERED`** followed by **`STARTUP COMPLETE`** after real model readiness and successful registration. Keep the subprocesses/runtime running, then independently verify API readiness below. All these completion markers were observed, and independent local `/ready` returned 200 with `Qwen/Qwen3-4B-Instruct-2507`, `pritamdeka/S-PubMedBert-MS-MARCO`, 768 embedding dimensions, CUDA and Tesla T4. If Chrome's debugging banner is dismissed, the extension disconnects but the notebook may continue: re-share the existing tab rather than rerunning cells or treating a detached browser check as Colab failure.

`MONGODB_URI` is **not required** for this web acceptance test. Web retrieval uses SQL through ASP.NET, not the legacy Colab Mongo route. Do not configure ignored static Colab URL/key fallbacks in appsettings or the frontend.

## 6. Confirm it is actually live

On your computer:

```powershell
Invoke-RestMethod http://localhost:5100/health
Invoke-RestMethod http://localhost:5100/ready
```

The readiness call must return **HTTP 200**, `ready: true`, and ready database/Colab/pipeline checks, with real model/embedding/GPU metadata. A running notebook, HTTP health response or printed tunnel URL alone is not enough.

If readiness remains 503, check the safe dependency summary and notebook startup status. Do not paste raw errors containing secrets; report the cell number, package-resolution error category or sanitized message. If a site requires login, 2FA or a challenge, complete it yourself through handoff.

Keep the computer, API/Python/frontend services, API tunnel and Colab runtime running for authorized follow-up. The final readiness recheck after account B isolation still returned **200** with all dependencies ready and the same genuine model/embedding/GPU metadata. Source-ZIP and both PDF hashes remain unchanged. This completed local acceptance attempt contains failed and blocked checks below; readiness and synthetic integration passes are not clinical validation.

## What this setup still cannot fix

The synthetic rule-control test created and submitted an explicitly test-only rule, verified deterministic “would fire” simulation and separate enablement, then left it disabled/unapproved. The approval dialog requires clinician review; that assertion was **not** confirmed. A simulation is not live approved-rule firing, and the local fixture is not public clinical source evidence.

Two synthetic cases were created with exact saved expectations and run through the genuine Tests UI. **Both failed before pipeline results**, with execution-failure diffs and absent output (known-quote case: 6.0829 ms; deliberate unmet expectation: 10.2136 ms). Both used published v1; neither exercised answer comparison or produced a trusted draft test. The failed histories are preserved. Static code inspection identifies missing forwarded bearer authorization in the harness while Python `/analyze` requires it; a correlated live diagnosis has not been captured. Inputs, exception details and upstream response bodies are not included here.

**Successful draft publication remains blocked by application implementation.** `NoConfigTestRunProvider` is still registered and Workflow's draft-test button has no runner integration. An authenticated publication request for the exact untested draft v2 returned **409**; published v1 and its JSON stayed unchanged, v2 remained draft without a trusted run, and the UI publish control stayed disabled. This protective denial is not successful publication. Do not insert passing database records, trust client-written results or bypass owner-only exact-artifact publication to mark acceptance green. Actual publication needs the separately implemented trusted, persisted draft-bound runner.

Full audit results, failed checks and other remaining gaps: [final-audit.md](final-audit.md).
