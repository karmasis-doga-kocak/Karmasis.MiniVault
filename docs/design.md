# MiniVault V1 — Tasarım Spesifikasyonu

Kaynak: `tasks/MiniVault - MasterKey Yonetimi Analizi/2026-09-02-minivault-v1-design.md`
Tarih: 2026-09-02
Durum: V1 uygulandı. Uygulama sırasında yapılan spec düzeltmeleri bu dokümanda ilgili bölümlere
işlenmiştir; ayrı bir "düzeltmeler" eki yoktur. Bugünkü uygulama durumu için bkz. §12.

## 1. Amaç

Dağıtık Karmasis servislerinin (Web UI, eski API, Classic Collector, yeni Collector,
remote/container collector) MasterKey'i görmeden şifreli sırlara erişmesini sağlayan, on-prem,
internetsiz ortamda çalışan minimal bir sır deposu. MasterKey yalnızca MiniVault host'unda yaşar;
diğer servisler `Karmasis.MiniVault.Client` paketi ile M2M kimlik doğrulayarak sırları alır.

## 2. Kapsam

### 2.1. V1'e giren

- `Karmasis.Cryptography` paketine DEK/KEK anahtar hiyerarşisi, AES-GCM, HKDF ve Shamir eklenmesi.
- `MiniVault.Server`: net10 minimal API, EF Core, SQL Server; `init`, `recover`, `rotate-dek`,
  `migrate`, `client` ve `role` CLI komutları.
- M2M kimlik doğrulama: client id + secret → kısa ömürlü JWT.
- Yetkilendirme: rol tabanlı, scope (isim öneki) + read/write.
- Recovery key: `single` ve `shamir` modları, `init` anında seçilir.
- MasterKey koruması: Windows'ta DPAPI machine-scope, container'da env variable.
- `Karmasis.MiniVault.Client` (netstandard2.0): factory + DI, şifreli lokal cache, offline başlangıç.
- İki dağıtım çıktısı: Windows Service (script + Advanced Installer setup) ve Linux container image.
- Audit log.
- Dokümantasyon: `README.md`, `docs/client.md`, `docs/operations.md`, bu doküman.

### 2.2. V1'e girmeyen

mTLS / client sertifikası, Kerberos, admin HTTP API'si, yönetim UI'ı, HSM provider, dynamic secrets,
otomatik DEK rotasyonu zamanlayıcısı, Windows container, Classic Collector entegrasyonunun kendisi.

## 3. Genel mimari

```
+--------------------+        HTTPS (JWT)        +---------------------+
| Classic Collector  | ------------------------> |  MiniVault.Server   |
| (net48, Ninject)   |  Karmasis.MiniVault.Client|  net10 minimal API  |
+--------------------+                           |                     |
+--------------------+                           |  IMasterKeyProvider |
| Web UI / yeni svc  | ------------------------> |   DPAPI | Env       |
| (net8+, DI)        |                           |                     |
+--------------------+                           |  KEK -> DEK -> Sır  |
                                                 +----------+----------+
                                                            | EF Core
                                                 +----------v----------+
                                                 |  SQL Server         |
                                                 |  MiniVault DB       |
                                                 +---------------------+
```

Sırrın yolculuğu: istemci `GET /v1/secrets/{name}` → sunucu JWT'den rolleri okur, önek eşleşmesiyle
yetkiyi kontrol eder → sırrın `DekVersion`'ına ait sarılmış DEK'i KEK ile açar → ciphertext'i DEK ile
çözer (sır adı associated data'dır) → TLS içinde düz değeri döner → audit kaydı.

Repo düzeni:

```
src/MiniVault.Server/                       net10, minimal API + CLI (assembly adı: minivault)
src/MiniVault.Client/                       netstandard2.0 nuget
src/MiniVault.Client.DependencyInjection/   netstandard2.0 nuget, MS.Extensions.DI.Abstractions
src/MiniVault.Contracts/                    netstandard2.0, DTO'lar (Server ve Client ortak)
test/MiniVault.Server.Tests/
test/MiniVault.Client.Tests/
deploy/windows/                             install.ps1, uninstall.ps1
setups/AdvancedInstaller/                   .aip + net48 custom action projesi
docker/                                     Dockerfile, docker-compose.yml
docs/                                       client.md, operations.md, design.md
README.md, azure-pipelines.yml
```

## 4. Karmasis.Cryptography eklemeleri (branch `KS-3316-DEK`)

Mevcut sınıflar (`AesEncryption`, `DataProtection`, `SymCrypto`, `HashProvider`) değişmez. Yeni
namespace `Karmasis.Cryptography.Keys`:

| Sınıf | API | Not |
|---|---|---|
| `KeyGenerator` | `byte[] GenerateKey(int sizeInBytes = 32)` | `RandomNumberGenerator` |
| `AeadCipher` | `byte[] Encrypt(byte[] plain, byte[] key, byte[]? aad = null)`, `byte[] Decrypt(byte[] blob, byte[] key, byte[]? aad = null)` | AES-256-GCM; çıktı `nonce(12) + tag(16) + ciphertext`; BouncyCastle ile tek implementasyon (net48'de yerleşik GCM yok) |
| `KeyWrapper` | `byte[] Wrap(byte[] dek, byte[] kek)`, `byte[] Unwrap(byte[] wrapped, byte[] kek)` | `AeadCipher` üzerine ince sarmalayıcı; niyet kodda görünür |
| `KeyDerivation` | `byte[] FromPassword(string password, byte[] salt, int iterations)`, `byte[] Hkdf(byte[] ikm, byte[] salt, string info, int length)` | PBKDF2-SHA256, 32 byte, varsayılan 100.000 iterasyon; HKDF RFC 5869 SHA-256 |
| `ShamirSecretSharing` | `byte[][] Split(byte[] secret, int shares, int threshold)`, `byte[] Combine(byte[][] shares)` | GF(256), bağımlılıksız; parça = `index(1) + data(n)`; 2 ≤ threshold ≤ shares ≤ 255 |

Diğer:

- `TargetFrameworks`'e `net10.0` ve `netstandard2.0` eklenir. `netstandard2.0` zorunludur; aksi
  halde Client paketi (netstandard2.0) bu pakete referans veremez.
- Testler: AES-GCM ve HKDF için RFC test vektörleri, round-trip, yanlış anahtar / tag bozulmasında
  exception, Shamir'de `threshold-1` parça ile yanlış sonuç ve `threshold` parça ile doğru sonuç.
- `InvariantGlobalization` kullanılmaz: `Microsoft.Data.SqlClient` invariant modda çalışmaz. Gerçek
  binary'yi process olarak çalıştıran bir smoke test bu regresyonu yakalar.

## 5. MiniVault.Server

### 5.1. Proje yapısı

Tek proje, `System.CommandLine` ile komutlar. Komut yoksa sunucu başlar; ayrı bir `serve` alt komutu
**yoktur**. Komutlar: `init`, `recover`, `rotate-dek`, `migrate`,
`client add|remove|assign|enable|disable|list`, `role add|remove|grant|list`.

Klasörler: `Keys/` (provider'lar, `KeyHierarchy`, `DataKeyRing`), `Data/` (DbContext, entity'ler,
migration'lar), `Auth/` (token üretimi, doğrulama, `Authorizer`, `ClientDirectory`), `Api/` (endpoint
grupları, hata işleme), `Cli/`, `Audit/`, `Secrets/`, `Vault/`, `Hosting/`.

CLI kuralları:

- `--Section:Key value` biçimindeki konfigürasyon override'ları System.CommandLine'a girmeden
  ayıklanır; geriye kalan bilinmeyen seçenekler parse hatası verir. Ayrım nettir: operatör
  seçenekleri (`--recovery`, `--force`, `--master-key-from-env`, ...) hiç iki nokta içermez.
- Operatör hataları (`VaultException`, `MasterKeyUnavailableException`, SQL bağlantı hataları) stack
  trace değil tek satır `Error: ...` üretir; çıkış kodu 1.
- Açılış hataları da tek satır okunabilir mesaj üretir; sunucu süreci 3 ile çıkar (servis/konteyner
  yeniden başlatma döngüsünde `sc.exe query` ve `docker logs` anlamlı bir şey gösterir).

### 5.2. MasterKey provider

```csharp
public interface IMasterKeyProvider
{
    string Name { get; }
    bool CanStore { get; }
    bool Exists();
    byte[] GetKek();
    void Store(byte[] kek);
}
```

| Provider | Platform | Kaynak | Config |
|---|---|---|---|
| `DpapiMasterKeyProvider` | Windows | `%ProgramData%\MiniVault\masterkey.bin`, DPAPI `LocalMachine` scope | `MasterKey:Provider = Dpapi`, konum `MasterKey:Path` ile değiştirilebilir |
| `EnvironmentMasterKeyProvider` | Linux/container | `MINIVAULT__MASTERKEY` (base64, 32 byte) | `MasterKey:Provider = Environment`; `CanStore = false` |

Kullanıcı parola girdiyse (`init --master-key <parola>` veya `--master-key-from-env`),
`KeyDerivation.FromPassword(parola, salt)` ile 32 byte KEK türetilir; salt `VaultMetadata.KekSalt`,
iterasyon sayısı `VaultMetadata.KekIterations` kolonunda durur (iterasyon sayısının saklanması,
ileride artırılabilmesi için zorunludur). Provider her zaman 32 byte ham KEK döner; parola/rastgele
ayrımı `init`'te biter.

Dosya ACL'i tasarımın parçasıdır: `masterkey.bin` ve `%ProgramData%\MiniVault` dizini kalıtım kapalı
ve yalnızca SYSTEM + Administrators + kuran kullanıcı full control ile oluşturulur. DPAPI
`LocalMachine` kapsamı tek başına aynı makinedeki diğer yerel kullanıcılara karşı koruma vermez. ACL
yeniden uygulanırken kurulumun verdiği açık (kalıtılmamış) izinler korunur ve dosyaya taşınır, böylece
`install.ps1 -ServiceAccount` ile verilen servis hesabı (RX) anahtar dosyasını okuyabilir; Everyone,
Users, Authenticated Users gibi geniş SID'ler taşınmaz.

### 5.3. Anahtar hiyerarşisi

- `DataKeys`: `Version`, `WrappedByMaster`, `WrappedByRecovery`, `IsActive`, `CreatedAt`.
  `IsActive` üzerinde filtreli unique index (`[IsActive] = 1`) vardır: "tek aktif DEK" invariant'ı
  şema tarafından da garanti edilir.
- Sır yazılırken aktif DEK kullanılır, sır satırı `DekVersion` taşır.
- `VaultMetadata.RecoveryKeyWrappedByMaster`, recovery key'in mevcut KEK ile sarılmış kopyasıdır.
  `rotate-dek` yeni DEK'i recovery key ile de sarmak zorundadır; bu kopya sayesinde operatör girdisi
  gerekmez. KEK olmadan işe yaramaz ve KEK her değiştiğinde yeniden sarılır; recovery key veritabanında
  hiçbir zaman düz durmaz.
- DEK rotasyonu (`minivault rotate-dek`): yeni DEK üret, KEK ve recovery key ile sar, aktif yap; eski
  DEK'ler okumak için kalır. Verinin yeniden şifrelenmesi V1'de yoktur. Çalışan sunucu anahtar
  halkasını açılışta yükler, bu yüzden rotasyondan sonra servis yeniden başlatılır.
- MasterKey değişimi (`recover --new-master-key`): tüm `WrappedByMaster` kolonları yeni KEK ile
  yeniden yazılır; sırlar dokunulmaz.

### 5.4. init / recover / migrate

`minivault init --recovery single|shamir [--shares n --threshold k] [--master-key <parola> |
--master-key-from-env] [--out <dosya>] [--force]`

1. DB'ye bağlanır, migration uygular. `VaultMetadata` doluysa reddeder.
2. MasterKey: verilmişse paroladan türetir, yoksa rastgele 32 byte üretir. Provider'da zaten bir
   MasterKey varsa (`Exists()`) `--force` verilmeden reddedilir; aynı host'taki başka bir vault'un
   anahtarı kazara ezilmez.
3. Recovery key: rastgele 32 byte. `single` modunda base64 tek çıktı; `shamir` modunda `Split(n, k)`
   ile n parça, her biri base64.
4. İlk DEK üretilir, KEK ve recovery key ile sarılır, `DataKeys`'e yazılır.
5. `VaultMetadata`: `RecoveryMode`, `Shares`, `Threshold`, `KekSalt`, `KekIterations`,
   `RecoveryKeyWrappedByMaster`, `InitializedAt`.
6. **Sıralama kuralı:** KEK provider'a, DB satırı commit edilmeden ÖNCE yazılır. `Store` başarısızsa
   vault "initialized" kalmaz; yarım kalan dosya sonraki `init`'te üzerine yazılır.
7. Recovery çıktısı stdout'a ve `--out` verildiyse dosyaya gider. Dosya, yalnızca mevcut kullanıcının
   okuyabileceği ACL ile ve `CreateNew` ile (üzerine yazmadan) oluşturulur. Recovery key DB'ye
   yazılmaz.

Parola, `--master-key-from-env` ile `MINIVAULT_INIT_MASTER_KEY` ortam değişkeninden okunur ve
okunduğu anda sürecin ortamından silinir; böylece komut satırına, servis `ImagePath`'ine, MSI verbose
log'una veya shell geçmişine hiç düşmez. Otomatik kurulumlarda tercih edilen yol budur.

`minivault recover --new-master-key <parola|auto> [--recovery-key <b64> | --share <b64> ...]`

1. Moda göre tek key veya k parça alır; Shamir'de `Combine`. İkisinden tam olarak biri verilmelidir.
2. Tüm DEK'leri `WrappedByRecovery` üzerinden açar.
3. Yeni KEK ile `WrappedByMaster` kolonlarını ve `RecoveryKeyWrappedByMaster`'ı yeniden yazar.
4. **Sıralama kuralı:** `recover` önce rewrap'i commit eder, sonra provider'a yazar. Yeni KEK
   saklanamazsa `VaultException` mesajı yeni KEK'i base64 olarak taşır; recovery materyali
   `WrappedByRecovery` dokunulmadığı için geçerli kalır.
5. Audit kaydı.

`minivault migrate` bekleyen EF Core migration'larını uygular. Boş veritabanında da (şemayı kurar),
güncel veritabanında da (no-op) güvenlidir; upgrade prosedürünün parçasıdır. Audit satırı
`MigrateAsync`'ten sonra ve kendi `SaveChanges`'i ile yazılır, uygulanan migration adlarını taşır.

Sunucu (komutsuz çalıştırma): `VaultMetadata` yoksa veya KEK ile aktif DEK açılamıyorsa açık hata
mesajıyla durur.

### 5.5. Veri modeli (EF Core, SQL Server)

| Tablo | Kolonlar |
|---|---|
| `VaultMetadata` | `Id` (tekil satır), `RecoveryMode`, `Shares`, `Threshold`, `KekSalt`, `KekIterations`, `RecoveryKeyWrappedByMaster`, `InitializedAt` |
| `DataKeys` | `Version` (PK), `WrappedByMaster`, `WrappedByRecovery`, `IsActive`, `CreatedAt` |
| `Secrets` | `Name` (PK, max 256, `/` ile hiyerarşi), `Ciphertext`, `DekVersion`, `ContentType` (max 128), `Version`, `UpdatedAt`, `UpdatedBy`, `RowVersion` |
| `Clients` | `ClientId` (PK, max 128), `SecretHash`, `SecretSalt`, `SecretIterations`, `Enabled`, `CreatedAt` |
| `Roles` | `Name` (PK, max 128), `Description` |
| `RoleRules` | `Id`, `RoleName`, `Scope` (önek, ör. `dataskope/collector/`), `Permission` (`Read`/`Write`) |
| `ClientRoles` | `ClientId`, `RoleName` |
| `AuditLog` | `Id`, `Timestamp`, `ClientId`, `Action`, `SecretName`, `Success`, `RemoteIp`, `Detail` |

Şema kuralları:

- Sır adları ve rol scope'ları **büyük/küçük harfe duyarlıdır**: `Secrets.Name` ve `RoleRules.Scope`
  kolonları `Latin1_General_100_BIN2` collation ile tanımlanır. AAD ve `Authorizer` ordinal
  karşılaştırdığı için veritabanı da öyle karşılaştırmalıdır.
- `DataKeys.IsActive` üzerinde filtreli unique index (`[IsActive] = 1`).
- `RoleRules (RoleName, Scope)` unique: aynı scope'a ikinci bir kural eklenmez, `grant` mevcut kuralı
  değiştirir.
- `Secrets.RowVersion` bir `rowversion` eşzamanlılık token'ıdır; eşzamanlı yazım `409 conflict`
  üretir.
- `AuditLog.Timestamp` üzerinde index; `Detail` 512 karakterde kesilir.
- `ClientRole.Role` navigation'ı yetkilendirme sorgusu için gereklidir.
- `EnableRetryOnFailure` kullanıldığında açık transaction'lar execution strategy içinde çalıştırılır.

Sır değeri opak `byte[]`'tır (en fazla 1.048.576 byte); `ContentType` serbest metindir
(`application/x-pkcs12`, `text/plain`) ve istemciye yalnızca ipucudur.

### 5.6. HTTP API

Tüm endpoint'ler HTTPS, `/v1` öneki. Yetki gerektirenler `Authorization: Bearer <jwt>`.

| Metod | Yol | Yetki | Davranış |
|---|---|---|---|
| POST | `/v1/auth/token` | — | Body `{clientId, clientSecret}` → `{accessToken, expiresIn}`; varsayılan 15 dk JWT |
| GET | `/v1/secrets/{name}` | Read | `{name, value(base64), contentType, version, updatedAt}`; `ETag: "<version>"`, `If-None-Match` → 304 |
| PUT | `/v1/secrets/{name}` | Write | Body `{value(base64), contentType}` → 200 `{version}` |
| DELETE | `/v1/secrets/{name}` | Write | 204 |
| GET | `/v1/secrets?prefix=` | Read (prefix'e) | `[{name, version, updatedAt}]`, değer yok |
| GET | `/v1/health` | — | 200 `{status, initialized, activeDataKeyVersion}` |

Hata kodları: 401 `unauthorized` (token yok/geçersiz/süresi dolmuş veya kimlik bilgisi yanlış), 403
`forbidden` (rol izin vermiyor), 404 `not_found`, 400 `invalid_request` (isim geçersiz, body hatalı,
değer base64 değil, boyut aşımı), 409 `conflict` (eşzamanlı değişiklik), 503 `vault_unavailable`
(KEK/DB erişilemiyor), 500 `internal_error`.

Ek kurallar:

- HTTP hata gövdeleri **her zaman** `ErrorResponse` şeklindedir; 405 (yol var, metot yok) ve 415
  (gövde `application/json` değil) dahil. Tek istisna 429 (gövdesiz) ve 499 (istemci bağlantıyı
  kapattı, hiçbir şey gönderilmez).
- 400 yanıtının `detail` alanı yalnızca bilinçli fırlatılan `SecretValidationException` mesajını
  taşır; framework veya exception metni asla istemciye gitmez.
- 304 yanıtı 200'ün taşıyacağı `ETag` başlığını taşır; `If-None-Match` zayıf tag'leri ve `*` değerini
  kabul eder (vault'ta versiyon başına tek temsil vardır).
- `GET /v1/secrets?prefix=` için prefix ayrıca doğrulanır (≤256 karakter, ad alfabesi + `/`); prefix
  bir sır adı değildir, segment ortasında bitebilir. Boş prefix yasaldır ve boş scope yetkisi ister.
- Sağlık ucu anonimdir ve `activeDataKeyVersion` döner (kabul edilen düşük riskli bilgi). Hiçbir zaman
  hata statüsü dönmez; `initialized: false` açılış sorununun işaretidir.

### 5.7. Kimlik doğrulama ve yetkilendirme

- Client secret: `minivault client add <id>` ile rastgele 32 byte üretilir, base64 bir kez gösterilir,
  DB'de PBKDF2-SHA256 hash + salt olarak durur; iterasyon sayısı (varsayılan 100.000)
  `Clients.SecretIterations` kolonunda saklanır, böylece ileride artırılabilir.
- JWT: HS256, imza anahtarı `Hkdf(kek, salt: "jwt", info: "minivault-jwt", 32)`. Ayrı bir sır
  yönetilmez; MasterKey değişince tokenlar geçersiz olur (kabul edilir). Claim'ler: `sub` = clientId,
  `role` = rol adları, `exp`. Doğrulamada `ValidAlgorithms = [HS256]` pinlenir, böylece başka bir
  algoritma adı taşıyan token hiç doğrulayıcıya girmez.
- Yetki kontrolü tek fonksiyon: `Authorizer.HasPermission(IEnumerable<RoleRule> rules, string
  secretName, Permission p)`; `secretName.StartsWith(rule.Scope, Ordinal)` ve
  `rule.Permission >= p` (`Write` `Read`'i kapsar). Roller token'dan, kurallar rol değişiminde
  yenilenen cache'ten okunur.
- Her sır erişimi ve her başarısız login `AuditLog`'a yazılır. Geçersiz veya eksik bearer token'lar
  endpoint'e hiç ulaşmaz, bu yüzden ayrıca `token.rejected` satırı üretirler
  (`ClientId = "(anonymous)"`, çağıranın IP'si, reddetme nedeni).
- Audit satırları her zaman kendi `DbContext`'inde (`IDbContextFactory`) yazılır; başarısız bir
  yazmanın audit'i, başarısız `SaveChanges`'i tekrar oynatmamalıdır. Böylece geri alınmış bir işlem
  de audit izini bırakır.
- `/v1/auth/token` dakikada `Token:LoginRateLimitPerMinute` (varsayılan 30) istekle sınırlıdır, sunucu
  süreci başına sabit bir dakikalık pencerede sayılır. Diğer uçlar sınırlanmaz; zaten token isterler.

### 5.8. Konfigürasyon

`appsettings.json` (Windows'ta ayrıca `%ProgramData%\MiniVault\appsettings.json`), ardından ortam
değişkenleri, ardından `--Section:Key value` komut satırı override'ları.

```json
{
  "ConnectionStrings": { "MiniVault": "Server=.;Database=MiniVault;Integrated Security=true;TrustServerCertificate=true" },
  "MasterKey": { "Provider": "Dpapi", "Path": null },
  "Tls": {
    "Url": "https://0.0.0.0:8200",
    "Certificate": { "Path": null, "Password": null, "Thumbprint": null, "StoreName": "My", "StoreLocation": "LocalMachine" },
    "AllowDevelopmentCertificate": false
  },
  "Token": { "LifetimeMinutes": 15, "LoginRateLimitPerMinute": 30 }
}
```

HTTP endpoint tanımlanmaz; sunucu yalnızca HTTPS dinler. `Kestrel:Endpoints` /
`Kestrel:EndpointDefaults` konfigürasyonu **reddedilir** (sessizce yok sayılmaz), `ASPNETCORE_URLS`,
`--urls`, `ASPNETCORE_HTTP_PORTS` ve `ASPNETCORE_PREFERHOSTINGURLS` yok sayılır. Tam anahtar listesi
için `docs/operations.md`, "Quick reference".

## 6. Karmasis.MiniVault.Client

### 6.1. Paketler

- `Karmasis.MiniVault.Client` — `netstandard2.0`. Bağımlılıklar: `System.Text.Json`,
  `Karmasis.Cryptography` (HKDF, AES-GCM), `MiniVault.Contracts`.
- `Karmasis.MiniVault.Client.DependencyInjection` — `netstandard2.0`. Ek bağımlılık:
  `Microsoft.Extensions.DependencyInjection.Abstractions`, `Microsoft.Extensions.Options`.

### 6.2. Genel API

```csharp
public interface IMiniVaultClient : IDisposable
{
    Task<Secret> GetSecretAsync(string name, CancellationToken ct = default);
    Task<int> SetSecretAsync(string name, byte[] value, string contentType = null, CancellationToken ct = default);
    Task DeleteSecretAsync(string name, CancellationToken ct = default);
    Task<IReadOnlyList<SecretListItem>> ListSecretsAsync(string prefix, CancellationToken ct = default);
    event EventHandler<CacheServedEventArgs> SecretServedFromCache;   // Stale = true ise MaxCacheAge aşılmış
}

public sealed class Secret
{
    public string Name { get; }
    public byte[] Value { get; }
    public string ContentType { get; }
    public int Version { get; }
    public DateTimeOffset UpdatedAt { get; }
    public string AsString();   // UTF-8
}

public static class MiniVaultClientFactory
{
    public static IMiniVaultClient Create(MiniVaultOptions options);
}

// DI paketi: TryAddSingleton; hem Action<MiniVaultOptions> hem IConfiguration overload'u
public static IServiceCollection AddMiniVaultClient(this IServiceCollection services, Action<MiniVaultOptions> configure);
public static IServiceCollection AddMiniVaultClient(this IServiceCollection services, IConfiguration section);
```

Factory'nin döndürdüğü nesne thread-safe'tir ve singleton olarak tutulması beklenir; tek `HttpClient`,
token cache'i, memory ve disk cache'i içerir.

`MiniVaultOptions.Validate()` kuralları (hepsi başlangıçta, ilk istekte değil, `ArgumentException`
fırlatır): `BaseUrl` mutlak ve `https://` zorunlu (`AllowInsecureHttp` yalnızca geliştirme için),
`ClientId` `^[A-Za-z0-9._-]{1,128}$` (sunucunun kuralı; aynı zamanda id'yi cache dosya adı olarak
güvenli kılar), `ClientSecret` boş olamaz, `RefreshInterval` verilmişse ≥ 1 sn, `MaxCacheAge` > 0,
`Timeout` 1 sn – 1 gün, `ServerCertificateThumbprint` verilmişse 40 hex (ayraçlar ve görünmez
karakterler atılır; geçersiz bir pin sessizce atlanmaz, başlangıçta hata verir).

### 6.3. Akış

`GetSecretAsync(name)`:

1. Memory cache'te kayıt varsa ve `RefreshInterval` tanımlıysa doğrudan dön (arka plan yeniler).
   Kayıt `MaxCacheAge`'i aşmışsa yine de canlı koşullu GET denenir.
2. Token yok veya süresi dolmak üzereyse `POST /v1/auth/token`. Kısa ömürlü token'larda (≤60 sn)
   ömrün yarısı cache'lenir.
3. `GET /v1/secrets/{name}`, memory'de kayıt varsa `If-None-Match: "<version>"`. `ETag` sunucudan
   geldiği gibi saklanır ve aynen geri gönderilir.
4. 200 → memory ve disk cache güncellenir, dön. 304 → memory'dekini dön; bu "onaylanmış canlı okuma"
   sayılır, cache olayı tetiklenmez.
5. 401 → bir kez token yenile ve tekrar dene; yine 401 → `MiniVaultAuthException`. 403 →
   `MiniVaultForbiddenException`. 404 → `MiniVaultNotFoundException`. Bunlar cache'e düşmez. 401'de
   yalnızca hatayı üreten token geçersiz kılınır, böylece eşzamanlı 401 fırtınası tek login'e iner.
6. Ağ hatası, timeout, 5xx, 429 → cache'te kayıt varsa dön ve `SecretServedFromCache` tetikle
   (`Stale` = alınma zamanı `MaxCacheAge`'i aştı); yoksa `MiniVaultUnavailableException`.

`SetSecretAsync` yazılan değeri lokal cache'e koyar (offline başlangıç korunur ve sunucudaki değer
zaten odur). `DeleteSecretAsync` kaydı cache'ten siler. Arka plan yenilemede 404 (sır silindi) veya
403 (yetki geri alındı) alan kayıtlar cache'ten atılır. Arka plan yenileme yalnızca daha önce en az
bir kez okunmuş sırları tazeler; listeleme kullanılmaz, çünkü bir istemci listeleme yetkisi olmadan
da okuma yetkisine sahip olabilir. Yetki kararı hiçbir zaman cache'lenmez.

### 6.4. Disk cache

- Dosya: `{CacheDirectory}/{ClientId}.cache`.
- Anahtar: `Hkdf(UTF8(ClientSecret), salt: UTF8(ClientId), info: "minivault-cache", 32)`.
- İçerik: JSON `[{name, value(base64), contentType, version, fetchedAt}]`, `AeadCipher.Encrypt` ile
  şifreli.
- Yazma atomik (temp dosya + rename); yalnızca bir şey gerçekten değiştiyse yeniden yazılır. Okuma
  hatası (bozuk dosya, yanlış anahtar) cache'i yok sayar ve loglar.
- Platform bağımsız; Windows'ta client secret'ın DPAPI ile saklanması tüketicinin sorumluluğudur ve
  `docs/client.md`'de örneklenir.

### 6.5. Hata sınıfları

`MiniVaultException` (taban; `StatusCode` ve `ErrorCode` taşır) → `MiniVaultAuthException` (401),
`MiniVaultForbiddenException` (403), `MiniVaultNotFoundException` (404),
`MiniVaultRequestException` (400/409), `MiniVaultUnavailableException` (ağ/timeout/5xx/429).
İstemci kodu tipe veya `ErrorCode`'a göre dallanır; `Message` veya `Detail` metnine göre asla.
Hata gövdesi boş gelebilir. İstek yolunda sır adı ham `/` ile taşınır (segment olarak encode edilmez).

## 7. Dağıtım

### 7.1. Windows

- `dotnet publish src/MiniVault.Server -p:PublishProfile=win-x64` (self-contained).
- `Host.UseWindowsService()`; sunucu konsoldan da çalışır. Servis olarak çalışırken content root
  binary'nin kendi klasörüdür.
- `deploy/windows/install.ps1` (6 adım, PowerShell 5.1 uyumlu, `icacls` SID ile; anahtarlar:
  `-NonInteractive`, `-SkipServiceStart`, `-SkipInit`, `-IgnoreHealthCheck`, `-SkipLogonRightGrant`,
  `-WhatIfMode`) ve `uninstall.ps1` (`-PurgeData`). Script yeniden çalıştırılabilir: servis varsa
  önce durdurulur ve sonunda `sc.exe config` ile yeniden yapılandırılır, yani aynı komut satırı hem
  kurar hem yükseltir. Sağlık kontrolü başarısızsa çıkış kodu 2. Servis hesabına gerekiyorsa
  `SeServiceLogonRight` verilir. Çalışma zamanı SQL rolü `db_datareader` + `db_datawriter`; şema
  değişikliği operatörün işidir.
- Advanced Installer projesi `setups/AdvancedInstaller/` altında XML (`.aip`) olarak yazılmıştır;
  custom action assembly'si net48'dir (SDK-style csproj, `Karmasis.MiniVault.sln` içinde, `dotnet build`
  ile derlenir). Kurulum `MV_*`
  property'leri ile yapılandırılır. Sır taşıyan property'ler ve deferred custom action veri
  property'leri (`WriteMachineConfig`, `RunInit`) `MsiHiddenProperties` listesindedir; `"` içeren
  değerler baştan reddedilir. Servis kurulumda başlatılır (`MsiServCtrl Event=163`). Upgrade'de
  `init` "already initialized" ise atlanır ve mevcut `appsettings.json` korunur
  (`MV_RECONFIGURE=1` ile yeniden yazılır).
- Setup, SQL login scriptini üretir; müşteri DBA'sı çalıştırır.

### 7.2. Container

- `docker/Dockerfile`: `mcr.microsoft.com/dotnet/aspnet:10.0` tabanlı, çok aşamalı, Linux, non-root
  kullanıcı (uid 1654), `curl` healthcheck, `NUGET_CONFIG` build arg. `ASPNETCORE_HTTP_PORTS`
  temizlenir.
- Env: `MINIVAULT__MASTERKEY`, `ConnectionStrings__MiniVault`, `Tls__Certificate__Path/Password`;
  PFX volume ile mount edilir ve konteyner kullanıcısı tarafından okunabilir olmalıdır.
- Init: `docker compose --profile init run --rm minivault-init`. MasterKey otomatik üretildiği için
  çıktıda base64 verilir, operatör env'e koyar.
- `docker-compose.yml` (`init` profili, `extra_hosts: host-gateway`); `.env` ve `certs/` git-ignore.

### 7.3. TLS

Yalnızca HTTPS; Kestrel tek bir `Listen` + `UseHttps` ile bağlanır. `Tls:Url` varsayılanı
`https://0.0.0.0:8200` ve host bir IP literal olmalıdır (hostname çözülmez). Sertifika ya PFX
(`Tls:Certificate:Path/Password`, Windows'ta `MachineKeySet`) ya da mağaza thumbprint'i
(`Thumbprint/StoreName/StoreLocation`, 40 hex, ayraçlar atılır, private key zorunlu) ile verilir;
tam olarak biri. `Tls:AllowDevelopmentCertificate` yalnızca Development ortamında geçerlidir; başka
ortamda `Tls:AllowDevelopmentCertificateOutsideDevelopment` (yalnızca otomatik test host'ları için)
olmadan açılış reddedilir. `TlsStartupCheck`, `VaultStartupCheck`'ten önce çalışır, böylece sertifika
sorunu vault sağlamken de raporlanır. Sunucu, HTTPS olmayan bir adres bağlandıysa açılışta durur.

Self-signed kullanımında istemci `ServerCertificateThumbprint` ile pinning yapar; client paketi
sertifika doğrulamayı kapatma seçeneği sunmaz. Pinning zincir doğrulamanın yerine geçer, üstüne
eklenmez: sunucu sertifikası yenilenince her pinli istemcinin ayarı güncellenmelidir.

### 7.4. CI

`azure-pipelines.yml`: build/test/coverage, win-x64 publish artifact'ı, üç client paketinin pack +
push'u, `buildDocker` / `buildMsi` değişkenleriyle korunan opsiyonel stage'ler, `image_version`
stage'ler arası output değişkeni. Feed dosyaları `nuget-dev.config` (dev/feature) ve
`nuget-release.config` (master). `Karmasis.Cryptography` artifactrepo(dev)'e çıkana kadar CI restore
başarısız olur.

## 8. Dokümantasyon

| Dosya | Okuyucu | İçerik |
|---|---|---|
| `README.md` | Repoya ilk bakan geliştirici | MiniVault nedir; bir sırrın yolculuğu; MasterKey nerede; recovery ne zaman; roller; repo düzeni; yerel çalıştırma |
| `docs/client.md` | Client paketini kullanan geliştirici | Kurulum (factory / DI / Ninject); sır okuma; PFX okuma; client secret'ı DPAPI ile saklama; cache davranışı ve olaylar; hata yakalama; Classic Collector örneği |
| `docs/operations.md` | Kurulum/operasyon | Quick reference; Windows ve Docker kurulumu; CLI referansı; MasterKey sağlayıcıları; yedekleme ve restore; TLS; HTTP API; upgrade; sorun giderme; üretim öncesi doğrulama listesi |
| `docs/design.md` | Ekip | Bu doküman |
| Cryptography `README.md` | Paket kullanıcısı | `Keys` namespace'i, örneklerle |

Dil: sade, kısa cümleler, her senaryo çalışan kod bloğuyla. Ürün dokümanları İngilizce, bu tasarım
dokümanı Türkçe.

## 9. Test

Tüm projelerde xUnit + Moq + Shouldly + Coverlet.

- **Cryptography:** RFC vektörleri (AES-GCM, HKDF), round-trip, bozuk tag / yanlış anahtar →
  exception, Shamir eşik davranışı, `FromPassword` determinizmi.
- **Server unit:** `Authorizer` (önek eşleşme, Write ⊇ Read, eşleşmeyen scope), `KeyHierarchy`
  (wrap/unwrap, rotasyon, recover), token üretimi/doğrulaması, TLS seçenekleri, CLI ayrıştırma.
- **Server integration:** `WebApplicationFactory` + LocalDB. Senaryo: `init` → sunucu → `client add`
  + `role` → login → put → get (ETag/304) → yetkisiz get 403 → `recover` ile MasterKey değiştir → get
  hâlâ çalışır. Ayrıca gerçek binary'yi process olarak çalıştıran bir smoke test.
- **Client:** `HttpMessageHandler` stub ile her dal (200/304/401 retry/403/404/5xx→cache/cache
  yok→exception), disk cache round-trip, stale olayı, bozuk cache dosyası.
- **Deployment:** `install.ps1` metin/parametre testleri; MSI custom action'ları için net48 test
  projesi.
- **Dokümantasyon:** `README.md`, `docs/operations.md` ve `docs/client.md` içindeki `minivault <komut>`
  ve `/v1/...` yolları koddaki komut ve route listelerine karşı doğrulanır.

## 10. Güvenlik sınırları (açıkça kabul edilenler)

- MiniVault host'unda SYSTEM yetkisi alan biri KEK'i çözer; HSM dışı her yazılım çözümünün sınırıdır.
- İstemci makinesini ele geçiren biri client secret ile o istemcinin rolündeki sırlara ulaşır; yetki
  modeli hasarı role sınırlar, `client remove` / `client disable` revoke eder. Elde tutulan token
  süresi dolana kadar (varsayılan 15 dk) çalışmaya devam eder.
- Recovery key DB'de yoktur; kaybı yalnızca MasterKey de kaybedilirse veri kaybıdır. DB yedeği ile
  recovery key ayrı saklanır.
- `Environment` provider'da master key sürecin ortam değişkenidir: `docker inspect` ve
  `/proc/<pid>/environ` onu düz metin gösterir. Docker soketine erişimi olan herkes master key'e
  sahiptir.
- Sağlık ucu anonimdir ve aktif DEK versiyonunu sızdırır; düşük riskli kabul edilmiştir.

## 11. İş sırası

1. Cryptography `Keys` (nuget → artifactrepodev).
2. Server: anahtar hiyerarşisi + `init`/`recover` + veri modeli.
3. Server: API + auth/authz + audit.
4. Client + DI paketi.
5. Dağıtım: Windows publish, install script, Advanced Installer, Dockerfile, CI.
6. Dokümantasyon (her adımın kendi dokümanı adımla birlikte; README, operations ve bu doküman sonda
   toparlanır).

Her adım ayrı implementasyon planı ve PR.

## 12. Uygulama durumu (2026-09-02)

**Hazır ve testli.**

- Sunucu: net10 minimal API, yalnızca HTTPS, DPAPI/Environment master key sağlayıcıları, KEK→DEK
  hiyerarşisi, AES-256-GCM (AAD = sır adı), JWT kimlik doğrulama, önek tabanlı roller, audit,
  rate limit, `ErrorResponse` şeklinde tek tip hata gövdeleri.
- CLI: `init`, `recover`, `rotate-dek`, `migrate`, `client add|remove|assign|enable|disable|list`,
  `role add|remove|grant|list`.
- HTTP API: `POST /v1/auth/token`, `GET|PUT|DELETE /v1/secrets/{name}`, `GET /v1/secrets?prefix=`,
  `GET /v1/health`.
- İstemci: `Karmasis.MiniVault.Client`, `Karmasis.MiniVault.Client.DependencyInjection`,
  `Karmasis.MiniVault.Contracts`.
- Dağıtım eserleri: win-x64 publish profili, `deploy/windows/install.ps1` + `uninstall.ps1`,
  `docker/Dockerfile` + `docker-compose.yml`, Advanced Installer `.aip` ve net48 custom action'ları,
  `azure-pipelines.yml`.
- Testler: `MiniVault.Server.Tests` 222 test (LocalDB gerektirir), `MiniVault.Client.Tests` 121 test
  (net10) / 120 test (net48), MSI custom action test projesinde 68 test (net48, ana solution'da,
  `dotnet test` ile koşar).

**Henüz doğrulanmamış.**

- Yükseltilmiş bir Windows host'ta gerçek kurulum: servis bağlamında `MachineKeySet` sertifika
  yükleme ve DPAPI `LocalMachine` unwrap hiç çalışmadı. Tam restore tatbikatı (yedek → yeni host →
  `recover` → eski sırrı oku) yapılmadı.
- MSI derlenmedi: Advanced Installer bu makinede yok. ICE doğrulaması, `/l*v` log'unda sır
  sızdırmama kabul testi ve major upgrade senaryosu bekliyor.
- CI hiç koşmadı: `Karmasis.Cryptography` (tercihen `netstandard2.0` içeren **stable** bir sürüm)
  `artifactrepo`/`artifactrepodev` feed'ine çıkmadan restore çalışmaz. Bu aynı zamanda
  `Karmasis.MiniVault.Client`'ın paylaşılan bir feed'e yayınlanmasının da ön koşuludur;
  `Karmasis.MiniVault.Contracts` aynı sürümle birlikte yayınlanır.
- Konteyner çalıştırılmadı: mount edilen PFX'in uid 1654 tarafından okunabilirliği ve
  `Kestrel__Endpoints__Http__Url` verildiğinde açılışın reddedilmesi doğrulanmadı.

Maddelerin tam listesi ve yapılış sırası `docs/operations.md`, "Pre-production checklist" bölümündedir.

### 12a. Ek (2026-09-03): MSI yapılandırma sayfaları

- Custom action projesi SDK-style `net48` csproj'a çevrildi ve test projesiyle birlikte
  `Karmasis.MiniVault.sln` içine alındı; ayrı setup solution'ı kaldırıldı. `dotnet build` / `dotnet test`
  kök dizinde her şeyi kapsar (custom action testleri yalnızca Windows'ta koşar).
- MSI'ın dört yapılandırma sayfası (`SqlDlg`, `ServiceDlg`, `TlsDlg`, `RecoveryDlg`) ve `MvErrorDlg`
  `.aip` içinde XML olarak yazıldı; ilk kurulumda `FolderDlg` ile `VerifyReadyDlg` arasına girer,
  upgrade'de (`OLDPRODUCTS`) atlanır, silent kurulumda görünmez. Her sayfa `Next`'te doğrular
  (hata → `MV_UI_ERROR` + `MvErrorDlg`). `threshold <= shares` kuralı MSI koşuluyla ifade
  edilemediğinden `ValidateProperties`, recovery üçlüsünü `InstallInitialize` öncesinde
  `MiniVaultCli.BuildInitArguments` kuralıyla doğrular; bu silent kurulumu da kapsar.
- Recovery material yine `%ProgramData%\MiniVault\recovery-<timestamp>.txt` dosyasına yazılır;
  deferred action UI'a taşıyamaz. `VerifyReadyDlg` özet, `ExitDialog` "dosyayı kopyala ve sil" notu
  gösterir.
- Sayfalar designer'da henüz açılmadı ve MSI derlenmedi. İlk designer oturumunda doğrulanacaklar
  `setups/AdvancedInstaller/README.md` "Dialogs" bölümünde; statik kontroller `verify-aip.ps1`
  bölüm 6'da.
