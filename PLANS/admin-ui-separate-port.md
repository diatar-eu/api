# 🛠️ Admin Felület Külön Porton - Implementációs Terv

**Verzió:** 1.0
**Dátum:** 2026-09-26
**Státusz:** Elfogadva, megvalósítás alatt

---

## 📋 Áttekintés

A `diatar-api` (MqttApi) jelenleg kizárólag publikus API-t ad. A cél egy **webes admin
felület a felhasználók kezelésére**, amely:

- **külön Kestrel porton** fut (`127.0.0.1:5262` alapértelmezésben),
- **kívülről nem érhető el** – a publikus porton egyetlen `/admin...` útvonal sem elérhető,
- **dynsec ellen hitelesít** – a konfigurációban vesszővel felsorolt admin felhasználónevek,
  a jelszót a broker ellenőrzi (a meglévő `IMqttPasswordVerifier` mechanizmus),
- **nulla új függőséggel** jár: nincs új NuGet csomag, nincs npm, nincs build lépés.

### Miért van erre szükség?

Jelenleg a felhasználók módosítása kizárólag a publikus API-n keresztül lehetséges, és csak
úgy, hogy a felhasználó ismeri a saját jelszavát. Emiatt:

- nincs lehetőség egy letiltott vagy megerősítésre váró fiók kézi megerősítésére,
- nincs lehetőség jelszó admin általi átírására,
- nincs áttekintés a fiókok állapotáról.

---

## 🏗️ Architektúra

```
                        ┌──────────────────────────────────────────┐
                        │              egy folyamat                 │
   internet ───────────►│  publikus listener (ASPNETCORE_URLS)    │
   (pl. 80→8080)        │    /api/v1/*, /scalar, /openapi, /health │
                        │    /admin* → 404                        │
                        └──────────────────────────────────────────┘
                        ┌──────────────────────────────────────────┐
   loopback csak ───────►│  admin listener (127.0.0.1:5262)        │
   (SSH alagút)          │    /admin, /admin/api/*, /admin/*.css    │
                        │    minden más → 404                      │
                        └──────────────────────────────────────────┘
                                        │
                                        ▼
                        ┌──────────────────────────────────────────┐
                        │  IDynsecService (egy MQTT kapcsolat)     │
                        └──────────────────────────────────────────┘
                                        │  $CONTROL/dynamic-security/v1
                                        ▼
                                  MQTT broker (dynsec)
```

A két listener ugyanazt a DI konténert használja, tehát **egyetlen** MQTT admin
kapcsolat jut a brokerhez – nincs duplikált kliens, nincs külön process.

---

## 🔒 1. Port- és Útvonal-Szeparáció

### Kestrel

`Program.cs` – a publikus listener mellé egy admin listener:

```csharp
builder.WebHost.ConfigureKestrel(k =>
{
    if (options.Enabled && IPAddress.TryParse(options.ListenAddress, out var address))
        k.Listen(address, options.Port);
});
```

A publikus portot **nem** nyúljuk hozzá: ha `ASPNETCORE_URLS` nincs beállítva, Kestrel
alapértelmezés szerint a 8080/5000-as portokat használja, és a `ConfigureKestrel` csak
*hozzáad* egy új endpointot.

### Port guard middleware

`Middleware/AdminPortGuardMiddleware.cs` – a pipeline **legelején**, a statikus file-ok előtt:

| Port | Útvonal | Eredmény |
|---|---|---|
| admin | `/admin...` | feldolgozva |
| admin | bármi más | 404 |
| publikus | `/admin...` | 404 |
| publikus | bármi más | feldolgozva |

Így az admin UI:
- nem érhető el a publikus porton (se a közvetlenül, se a reverse proxy-n át),
- nem kerül be az OpenAPI/Scalar dokumentációba sem (az csak a publikus endpointokat
  dokumentálja),
- a statikus assetek (`/admin/admin.css`, `/admin/admin.js`) is a `/admin` prefix alatt
  vannak, tehát ugyanaz a szabály vonatkozik rájuk.

---

## ⚙️ 2. Konfiguráció

### `appsettings.json` – új `Admin` szekció

```json
"Admin": {
  "Enabled": true,
  "ListenAddress": "127.0.0.1",
  "Port": 5262,
  "Usernames": "",
  "CookieSecret": "",
  "SessionMinutes": 480,
  "LoginAttemptLimit": 5,
  "LoginAttemptWindowMinutes": 5
}
```

A `Mqtt` / `Email` szekciók mintájára, `builder.Services.Configure<AdminOptions>(...)`
segítségével bindoljuk.

### `.env` (lokális, **nem** kerül a repóba – a `.gitignore` már kizárja)

```
Admin_Usernames=admin
Admin_CookieSecret=<openssl rand -base64 32>
```

Több admin: `Admin_Usernames=admin,operator`

### `docker-compose.yml` – ez megy a repóba

A `Mqtt__Host=${MQTT_HOST}` mintájára:

```yaml
ports:
  - "80:8080"
  - "127.0.0.1:${Admin_Port:-5262}:${Admin_Port:-5262}"
environment:
  - Admin__Usernames=${Admin_Usernames}
  - Admin__CookieSecret=${Admin_CookieSecret}
```

A `127.0.0.1:` prefix miatt a published port **a host loopback-jén** érhető el, nem a
szerver publikus IP-jén. SSH alagúttal:

```bash
ssh -L 5262:127.0.0.1:5262 user@szerver
# majd böngészőből: http://localhost:5262/admin
```

### `Dockerfile`

```
EXPOSE 8080 5262
```

### `Usernames` feldolgozás

- `,` szerinti split, trim, üres elemek kizárása
- `Ordinal` összehasonlítás – a dynsec felhasználónevek kis/nagybetűre érzékenyek
- üres/hiányzó lista → **fail-closed**: minden admin útvonal 503-at ad, indulási
  figyelmeztető loggal

### `CookieSecret`

- env-ből (`Admin__CookieSecret`) – `openssl rand -base64 32` érték
- hiányozva → induláskor 32 random bájt generálódik, figyelmeztető loggal
  (a sessionök minden újraindításkor érvénytelenek lesznek)

---

## 🔑 3. Hitelesítés – dynsec Ellen

A jelszó **sehol nem tárolódik** a konfigurációban, csak ellenőrizzük a brokeren.

### `Services/AdminAuthService.cs`

```
POST /admin/login  { username, password }
  │
  ├─ nincs engedélyezett admin név a configban?  → 503 (fail-closed)
  ├─ rate limit túllépve?                        → 429
  ├─ username nincs az engedélyezett listában?   → 401  (nem mérünk tovább)
  ├─ IMqttPasswordVerifier.VerifyAsync(...)     → 401  (általános üzenet)
  └─ siker → session cookie kiállítás + 302 az /admin oldalra
```

### Session cookie

```
érték:    username|expiryUnix|HMAC-SHA256(username|expiry, CookieSecret)
cookie:   diatar_admin  HttpOnly  SameSite=Strict  Secure(HTTPS esetén)  Path=/
```

- aláírás ellenőrzése `CryptographicOperations.FixedTimeEquals`-tel
  (a `UserEndpoints.cs` `TokenMatches` mintája)
- `SessionMinutes` (alap 480 = 8 óra) csúszó megújítás: minden sikeres kéréskor
  visszafejlesztésre kerül
- a `username` a cookie része (a HMAC miatt nem hamisítható)

### Védelem a `/admin/api` csoporton

Egy endpoint filter minden admin API-hívásnál ellenőrzi az allowlistet és az alárást,
különben `401 ApiResponse.Fail(...)`. A dynsec jelszót **nem** ellenőrizzük újra
kérésenként – az minden HTTP kérésre egy MQTT parancs lenne. A session 8 órás, a
kilépés/újraindítás/lejárat azonnali érvénytelenítés.

### Rate limit

IP-nként (`LoginAttemptLimit` hiba / `LoginAttemptWindowMinutes` perc), sikeres belépésnél
nullázva. Azért fontos, mert minden belépési kísérlet MQTT kapcsolatot nyit a brokeral.

### Önzárolás elleni védelem

Egy `disabled=true` dynsec felhasználó **nem tud MQTT-n kapcsolódni**, így belépni sem tud.
Emiatt a UI nem engedi a bejelentkezett admin saját fiókját letiltani vagy törölni –
kaland a bejelentkezés megőrzése érdekében.

---

## 🧩 4. Domain-Logika Kiemelése

`UserEndpoints.cs` jelenleg tartalmazza a felhasználókezelési logikát (email-ütközés
keresés, megerősítő email újraküldése, létrehozás). Ezt kiemeljük:

- `Services/IUserManagementService.cs` + `UserManagementService.cs`
- a `UserEndpoints` vékony delegálásra hízik → **a publikus API viselkedése változatlan**
- az admin ugyanazt a service-t használja, nincs duplikált logika

---

## 🔌 5. `IDynsecService` Bővítés

| Metódus | Dynsec parancs | Mire |
|---|---|---|
| `ListUsersDetailedAsync` | `listClients` + párhuzamos `getClient` (szemafor, ~8) | táblázatos felhasználólista |
| `SetDisabledAsync` | `modifyClient` (csak `disabled`) | felfüggesztés / újraengedélyezés, a tokenhez nem nyúl |
| `ForceVerifyAsync` | `createRole` (ha hiányzik) + `modifyClient` (`disabled=false`, token ürítés, `s-<user>` bekötése) | kézi megerősítés email nélkül |
| `SetEmailAsync` | `modifyClient` (csak `textname`) | e-mail módosítás token kényszerítés nélkül |

A meglévő `ChangeEmailAsync` (ami `disabled=true`-t állít) érintetlen marad, arra a
publikus tokenes flow-hoz van szükség.

ACL-lekérdezés (`getRole`) **nem** kerül be – a UI csak szerepneveket mutat.

---

## 🌐 6. Admin API

`Endpoints/AdminEndpoints.cs` – minden válasz `ApiResponse` szerint, hibák
401/404/409/503:

| Metódus | Útvonal | Leírás |
|---|---|---|
| GET | `/admin` | login oldal, vagy dashboard ha van érvényes session |
| POST | `/admin/login` | belépés |
| POST | `/admin/logout` | kijelentkezés, cookie törlés |
| GET | `/admin/api/users?query=&status=` | felhasználólista + kereső + szűrő |
| POST | `/admin/api/users` | létrehozás (`skipVerification` kapcsolóval) |
| POST | `/admin/api/users/{u}/verify` | kézi megerősítés |
| POST | `/admin/api/users/{u}/enable` | újraengedélyezés |
| POST | `/admin/api/users/{u}/disable` | letiltás |
| POST | `/admin/api/users/{u}/resend-verification` | megerősítő email újraküldése |
| POST | `/admin/api/users/{u}/password` | jelszó átírása |
| POST | `/admin/api/users/{u}/email` | e-mail beállítása |
| DELETE | `/admin/api/users/{u}` | törlés (a `s-<username>` szereppel együtt) |

`status` szűrő: `all` (alap) / `verified` / `pending` / `disabled`.

---

## 🎨 7. Admin UI

`wwwroot/admin/index.html` + `admin.css` + `admin.js` – **egyetlen lap**, vanilla JS:

- táblázat: felhasználónév, e-mail, állapot badge, szerepek
- szöveges kereső + állapot szűrő (kliensoldalon, azonnali)
- sor-akciók gombokkal, megerősítő dialógussal
- új felhasználó űrlap (megerősítő email kihagyása kapcsolóval)
- toast visszajelzések
- a bejelentkezett admin neve a fejlécben + kijelentkezés gomb

Nincs npm, nincs build lépés, nincs új NuGet csomag.

---

## 📝 8. Audit Napló

`ILogger<AdminEndpoints>` (`Diatar.Admin` kategória): ki, mit, cél felhasználó, IP,
eredmény. **Jelszó és token soha nem kerül bele.**

Opcionális `Admin__AuditLogPath` → soronkénti JSON-lines fájl.

---

## 🌍 9. Lokalizáció

`Localization/hu.json` + `en.json`: új `admin_*` kulcsok. A `LocalizationService`
hiányzó kulcsnél a kulcsnevet adja vissza, tehát a fordítás nem törik el.

---

## ✅ 10. Ellenőrzés

Broker nélkül futtatható:

1. `dotnet build`
2. port szeparáció: `localhost:5261/api/v1/health` él; `localhost:5261/admin` → 404;
   `127.0.0.1:5262/admin` → login oldal; `127.0.0.1:5262/api/v1/...` → 404
3. auth ágak: nem engedélyezett username → 401; engedélyezett + hibás jelszó → 401;
   API cookie nélkül → 401; rate limit → 429; logout után → 401

**Nincs helyben tesztelve** (a kérés szerint): a dynsec-et érintő végpontok
(create / verify / delete / módosítás) valódi broker nélkül nem próbálhatók ki.

---

## 🔧 11. Megvalósítás közben kiderült döntések

**Kestrel: két listener, de a publikus is explicit.** Az ASP.NET Core-ban egyetlen
`kestrel.Listen(...)` hívás felülírja a hosting címeket (`ASPNETCORE_URLS`,
`ASPNETCORE_HTTP_PORTS`), ezért a publikus port elsűnt. A `Program.cs` ezért maga
bindolja a publikus címet is, ugyanabban a `ConfigureKestrel` blokkban.

**A publikus cím forrása a `Public:Url`** (`appsettings.json`, felülírható
`Public__Url` környezeti változóval), nem az `ASPNETCORE_URLS`. Így minden listener
egy helyen van leírva az `Admin:ListenAddress`/`Admin:Port` mellett, és a konténerben
nem keletkezik a `HTTP_PORTS`-fölülírás figyelmeztetés. Sorrend: `ASPNETCORE_URLS`
(operator override) → `Public:Url` → `ASPNETCORE_HTTP_PORTS`/`HTTPS_PORTS` →
`http://0.0.0.0:8080`.

**A `+`/`*` host nem valid URI**, tehát `Uri.TryCreate` nem használható rá: a
`EndpointBinding` a Kestrelhez hasonló kézi parse-t végez (`http://+:8080`,
`http://[::]:8080`, `http://localhost:5261`, alapértelmezett port, csupasz IPv6
elutasítva). Ha a publikus URL és az admin listener ugyanaz lenne, a Kestrel csak
zavaros bind-hibát adna induláskor, ezért ezt előre ellenőrzi és tiszta
`InvalidOperationException`-nel áll le.

**Egy figyelmeztetés megmarad:** `Overriding address(es) 'http://*:8080'`, mert a
.NET konténer image `ASPNETCORE_HTTP_PORTS=8080`-at állít be, amit a Kestrel
konfigurációból származó címnek tekint. A végpont valóban le van kötve
(`Now listening on: http://[::]:8080`), tehát ártalmatlan. Eltüntetéséhez
`ASPNETCORE_HTTP_PORTS=` (üresen) kellene a compose-ba.

**Konténerben a listen cím 0.0.0.0.** A Docker port publikálás a konténer eth0
címére DNAT-ol, nem a konténer loopbackjára, ezért `127.0.0.1`-re kötve a published
port halott (connection refused) volt. Ezért a compose `Admin__ListenAddress=0.0.0.0`-t
ad be, és a védelmet a `127.0.0.1:${Admin_Port}:${Admin_Port}` host mapping adja: a
admin a szerveren kívülről csak SSH alagúttal érhető el. Bare metal / systemd
üzemben a `127.0.0.1` marad a `ListenAddress` alapértéke. Induláskor az app kiírja a
tényleges admin címet, és nem-loopback cím esetén figyelmeztet a host mappingre.

**Assetek a build kimenetébe:** `AdminAssets\*` a Web SDK default `Content` globjaiba
nem esik bele, ezért `Content Include` (nem `Update`) kellett hozzá, különben a
`bin` alatt nincs `AdminAssets` mappa és minden asset 404.

**`IAdminAuthService` és `IMqttPasswordVerifier` singleton.** Scopedként a rate limit
számláló és a cookie-titok kérésenként újraindult volna (a rate limit így sosem
telítődött). A `UserManagementService` scoped marad, mert az `IEmailService` is az.

**Session cookie formátum:** `base64url(username) | expiry | HMAC-SHA256`, a
`SameSite=Strict`, `HttpOnly`, `Path=/admin` attribútumokkal. A `Secure` flag csak HTTPS
esetén kerül rá (SSH alagúton HTTP-n fut). A username base64url kódolás azért kell, mert
szó szerint `|` karakterrel nem szétválasztható a formátum, a `+`/szóköz pedig a
fejlécet törné. A keret maga URL-encodolja a cookie értékét kimenetkor és visszafejti
olvasáskor, ezt a formátum figyelembe veszi.

**JS login hiba:** az `api()` helper 401-nél a login oldalon is átirányított `/admin`-ra,
így a hibás jelszó üzenete eltűnt. A login hívás `stayOnFailure` opcióval marad a
formon, és a szerver által küldött lokalizált üzenetet mutatja.

**Ellenőrzés kiegészítése:** a session cookie round-trip, a hamisítás (másik user /
módosított lejár / hibás aláírás / másik titok), a rate limit IP-nként és a fail-closed
viselkedés egy `/tmp` alatt futó ideiglenes harnessszel lett ellenőrizve (a repóhoz nem
került tesztprojekt). End-to-end egy ugyanazzal a titokkal aláírt sütivel: `/admin` a
dashboardot adja, az API 401 helyett 503-at (broker nélkül), hamis sütivel 401.
