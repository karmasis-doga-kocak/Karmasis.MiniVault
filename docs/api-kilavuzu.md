# MiniVault API Kılavuzu

Bu kılavuz, MiniVault'a Postman (veya `curl.exe`) ile istek atıp secret okuyup yazacak, entegrasyon
test edecek veya bir hatayı elle yeniden üretecek ekip içindir. Kriptografi bilgisi gerektirmez.
Her endpoint tam haliyle, örnek istek ve yanıtıyla, "ne zaman kullanılır" açıklamasıyla verilmiştir.
Komut satırı yönetimi (client, rol, kurulum) için `docs/saha-kilavuzu.md`, derin teknik detay için
`docs/operations.md` (İngilizce) dosyalarına bakılır.

Hazır Postman koleksiyonu: `docs/minivault.postman_collection.json` (bkz. bölüm 2.3).

---

## 1. Beş dakikada MiniVault API

**Toplam altı endpoint vardır.** İkisi kimlik istemez, dördü token ister:

| Ne yapar | Method ve yol | Token gerekir mi |
|---|---|---|
| Sunucu ayakta mı? | `GET /v1/health` | Hayır |
| Giriş yap, token al | `POST /v1/auth/token` | Hayır |
| Secret yaz (oluştur veya güncelle) | `PUT /v1/secrets/{name}` | Evet |
| Secret oku | `GET /v1/secrets/{name}` | Evet |
| Secret'ları listele (değersiz) | `GET /v1/secrets?prefix=` | Evet |
| Secret sil | `DELETE /v1/secrets/{name}` | Evet |

**Akış:** Önce `client id` + `client secret` ile `/v1/auth/token`'a POST atılır, 15 dakika geçerli
bir token gelir. Sonra her istekte bu token `Authorization: Bearer <token>` başlığıyla gönderilir.
Token süresi dolunca aynı şekilde yenisi alınır.

**Her istekte geçerli ortak kurallar:**

- Adres her zaman `https://` ile başlar. Düz `http://` dinleyen bir port yoktur.
- İstek gövdesi ve yanıt gövdesi JSON'dur. Gövde gönderen isteklerde `Content-Type: application/json`
  başlığı zorunludur; yoksa `415` döner.
- Secret değeri her iki yönde de **base64** olarak taşınır. `hello` yazmak için `aGVsbG8=`
  gönderilir; okurken gelen `aGVsbG8=` çözülünce `hello` çıkar. Bu, elle istek atarken en çok
  takılınan adımdır; nedeni ve nasıl yapılacağı bölüm 1.1'de.
- Hatalar her zaman aynı biçimdedir: `{"error":"<kod>","detail":"<açıklama>"}`. `detail` her zaman
  bulunmaz. Kodların listesi bölüm 4'tedir.
- Secret adı ve scope eşleşmesi **büyük/küçük harfe duyarlıdır**. `Dataskope/x` ile `dataskope/x`
  farklı secret'lardır.

### 1.1. Neden base64? (önemli)

`value` alanına düz metin yazarsanız sunucu `Value must be base64-encoded.` hatasıyla reddeder.
Bu bir tercih değil, zorunluluktur; nedeni şudur.

**JSON yalnızca metin taşır.** Bir JSON alanına ne yazarsanız yazın, o bir karakter dizisidir.
Oysa secret her zaman metin değildir. Bir PFX sertifika dosyası, ikili bir anahtar veya bir lisans
dosyası 0 ile 255 arasında rastgele baytlardan oluşur: sıfır baytı, satır sonu, tırnak, geçersiz
karakter kodları, hepsi karışık halde. Bunu bir JSON string'ine olduğu gibi koyamazsınız; ya
ayrıştırıcı bozulur ya da baytlar yolda değişir ve karşı tarafa farklı bir dosya ulaşır.

**Base64 bu sorunu çözen standart yoldur.** Herhangi bir bayt dizisini alır, yalnızca
`A-Z a-z 0-9 + / =` karakterlerinden oluşan bir metne çevirir. Bu metin her JSON alanına sorunsuz
sığar; karşı taraf çözünce baytlar birebir geri gelir. Boyut yaklaşık üçte bir artar, o kadar.
Base64 bir **şifreleme değildir**, herkes çözebilir; yalnızca taşıma biçimidir. Şifrelemeyi sunucu
yapar: değer veritabanına yazılmadan önce AES-256-GCM ile şifrelenir, veritabanında düz hali yoktur.

**MiniVault'un tercihi tek bir `value` alanıdır.** Metin için ayrı, ikili için ayrı alan olsaydı
düz parolalar için base64'e gerek kalmazdı; ama iki ayrı yol, iki ayrı kural ve iki ayrı hata
durumu olurdu. Bunun yerine her değer bayt dizisi olarak ele alınır, base64 ile taşınır ve
`contentType` alanı "bu neydi" bilgisini yanına ekler: `text/plain` ise çözüp metin olarak
okursunuz, `application/x-pkcs12` ise çözüp PFX olarak dosyaya yazarsınız.

**Pratikte ne demek:**

| Saklamak istediğiniz | `value` alanına yazılacak | Nasıl elde edilir |
|---|---|---|
| `P@ssw0rd-2026` parolası | `UEBzc3cwcmQtMjAyNg==` | `[Convert]::ToBase64String([Text.Encoding]::UTF8.GetBytes("P@ssw0rd-2026"))` |
| `C:\cert.pfx` dosyası | `MIIKkQIBAzCCCk...` (uzun) | `[Convert]::ToBase64String([IO.File]::ReadAllBytes("C:\cert.pfx"))` |

Okurken tersi: gelen `value` çözülür, `[Text.Encoding]::UTF8.GetString([Convert]::FromBase64String("..."))`
metni, `[IO.File]::WriteAllBytes("C:\cert.pfx", [Convert]::FromBase64String("..."))` dosyayı verir.
Türkçe karakter içeren metinlerde her zaman UTF-8 kullanın; sunucu baytları olduğu gibi saklar,
hangi kodlamayla yazdıysanız o kodlamayla okumanız gerekir.

Bu zahmet yalnızca Postman veya `curl.exe` ile elle istek atana düşer. Client kütüphanesini kullanan
servisler base64 görmez: `GetSecretAsync` doğrudan çözülmüş değeri verir, yazarken de çevirmeyi
kütüphane yapar. Postman koleksiyonundaki `Secret - Write` isteği düz metni kendisi çevirir,
`Secret - Read` isteği de çözülmüş değeri Console'a yazar (bölüm 2.4).

**Secret adı kuralları** (`{name}` yerine yazılan kısım):

- `/` ile ayrılmış parçalardan oluşur: `dataskope/collector/db-password`.
- Her parça harf, rakam, nokta, tire, alt çizgi içerir. Boşluk, Türkçe karakter, `\` yoktur.
- Başında veya sonunda `/` olmaz, iki `/` yan yana gelmez, yalnızca noktadan oluşan parça (`..`)
  olmaz.
- En fazla 256 karakterdir.

---

## 2. Postman kurulumu

### 2.1. Sertifika doğrulamasını kapatma (test sunucuları için)

MiniVault kendinden imzalı veya geliştirme sertifikasıyla çalışıyorsa Postman istekleri
"SSL Error: Self signed certificate" ile reddeder. Bunu kapatmak için:

1. Postman'de **Settings** (sağ üstteki dişli) > **General** sekmesi.
2. **SSL certificate verification** anahtarını **OFF** yapın.

Üretim sunucusunda sertifika güvenilir bir CA'dan ise bu ayara dokunmayın.

### 2.2. Environment oluşturma

Adres ve kimlik bilgilerini her isteğe elle yazmamak için bir environment açın:

1. Sol menüde **Environments** > **+** ile yeni environment, adı örneğin `MiniVault Test`.
2. Şu değişkenleri ekleyin:

| Değişken | Örnek değer | Açıklama |
|---|---|---|
| `baseUrl` | `https://minivault.local:8200` | Sunucu adresi, sonunda `/` olmadan |
| `clientId` | `dataskope-collector` | `minivault client add` ile açılan client id |
| `clientSecret` | `8k3F2v9q...` | `client add` çıktısındaki client secret. **Current value** sütununa yazın, Initial value boş kalsın; böylece environment paylaşılırsa secret paylaşılmaz |
| `token` | (boş) | Token isteği bunu otomatik doldurur |
| `secretName` | `dataskope/collector/db-password` | Denemelerde kullanılacak secret adı |

3. Sağ üstteki environment seçicisinden `MiniVault Test`'i seçin.

### 2.3. Hazır koleksiyonu içe aktarma

1. **Import** > `docs/minivault.postman_collection.json` dosyasını seçin.
2. `MiniVault` adlı koleksiyon gelir; içinde bu kılavuzdaki her endpoint için bir istek vardır.
3. Koleksiyon, `Authorization` sekmesinde **Bearer Token** = `{{token}}` ayarıyla gelir. Token
   ve health istekleri bunu **No Auth** ile geçersiz kılar; diğer istekler koleksiyondan miras alır.
4. Token isteğinin **Scripts > Post-response** (eski sürümlerde **Tests**) sekmesinde şu script
   vardır; yanıttaki token'ı environment'a yazar, elle kopyalamak gerekmez:

```javascript
if (pm.response.code === 200) {
    pm.environment.set("token", pm.response.json().accessToken);
}
```

**Koleksiyonu kullanmadan elle istek yazıyorsanız** her token isteyen istekte **Authorization**
sekmesi > Type: **Bearer Token** > Token: `{{token}}` seçin. Başlığı elle yazmak isterseniz
**Headers** sekmesine `Authorization` = `Bearer {{token}}` ekleyin; ikisi aynı şeydir.

### 2.4. Değeri base64'e çevirme

Postman'in **Body** sekmesine düz metin değil base64 yazılır; nedeni bölüm 1.1'de. Çevirmek için
dört yol:

- **PowerShell:**

```powershell
[Convert]::ToBase64String([Text.Encoding]::UTF8.GetBytes("hello"))
# aGVsbG8=
[Text.Encoding]::UTF8.GetString([Convert]::FromBase64String("aGVsbG8="))
# hello
```

- **Hazır koleksiyon:** `Secret - Write` isteği bunu kendisi yapar. Koleksiyon değişkeni
  `secretValuePlain` alanına düz metni yazın; isteğin pre-request script'i göndermeden önce base64'e
  çevirip `secretValueBase64` değişkenine koyar. PFX gibi kendiniz çevirdiğiniz bir değeri
  göndermek için `secretValuePlain` alanını boş bırakıp `secretValueBase64` alanına yazın.
  Postman bu çevirmeyi kendiliğinden yapmaz; yalnızca Basic Auth başlığı için base64 üretir.

- **Kendi isteğinizde script:** Environment'ta `plainValue` değişkeni tanımlayın, isteğin
  **Scripts > Pre-request** sekmesine şunu koyun ve gövdede `{{valueBase64}}` kullanın:

```javascript
pm.variables.set("valueBase64", Buffer.from(pm.environment.get("plainValue"), "utf8").toString("base64"));
```

- **Dosya:** `[Convert]::ToBase64String([IO.File]::ReadAllBytes("C:\cert.pfx"))` ile bir PFX gibi
  ikili dosyayı da aynı alana koyabilirsiniz.

---

## 3. Endpoint'ler

Örneklerde sunucu adresi `https://minivault.local:8200`, Postman'de `{{baseUrl}}` karşılığıdır.

### 3.1. Sağlık kontrolü: `GET /v1/health`

**Ne işe yarar:** Sunucu ayakta mı ve kasa açık mı, tek bakışta söyler. Token istemez. Sorun
ararken ilk atılacak istektir.

**İstek:**

```
GET https://minivault.local:8200/v1/health
```

Başlık ve gövde yoktur.

**Yanıt (200):**

```json
{"status":"ok","initialized":true,"activeDataKeyVersion":1}
```

| Alan | Anlamı |
|---|---|
| `status` | Sunucu yanıt verebiliyorsa `ok`. |
| `initialized` | `true` ise kasa açık ve master key yüklü. `false` ise `minivault init` yapılmamış veya master key okunamamış; secret istekleri `503` alır. |
| `activeDataKeyVersion` | Yeni yazılan secret'ların şifrelendiği data key sürümü. `minivault rotate-dek` sonrası artar. Kasa açık değilse `0`. |

**Postman'de:** Koleksiyondaki `Health` isteği. Authorization sekmesi **No Auth**.

### 3.2. Token alma: `POST /v1/auth/token`

**Ne işe yarar:** Client kimliğiyle giriş yapar, 15 dakikalık token verir. Diğer dört endpoint bu
token olmadan çalışmaz.

**İstek:**

```
POST https://minivault.local:8200/v1/auth/token
Content-Type: application/json

{"clientId":"dataskope-collector","clientSecret":"8k3F2v9qA1zR7pC0eQ6nS4gU2y5T0hJ3W8lD1bXfM6o="}
```

**Yanıt (200):**

```json
{"accessToken":"eyJhbGciOiJIUzI1NiIsInR5cCI6IkpXVCJ9.eyJzdWIiOiJkYXRhc2tvcGUtY29sbGVjdG9yIiwicm9sZSI6ImNvbGxlY3Rvci1yZWFkZXIiLCJuYmYiOjE3NTcwMDAwMDAsImV4cCI6MTc1NzAwMDkwMCwiaWF0IjoxNzU3MDAwMDAwLCJpc3MiOiJtaW5pdmF1bHQiLCJhdWQiOiJtaW5pdmF1bHQifQ.q3Yw8w...","expiresIn":900}
```

| Alan | Anlamı |
|---|---|
| `accessToken` | Diğer isteklerde `Authorization: Bearer ...` olarak gönderilecek değer. İmzalı bir JWT'dir; içinde client id ve roller vardır, değiştirilemez. |
| `expiresIn` | Token ömrü, saniye. Varsayılan 900 (15 dakika). |

**Hatalar:**

| Durum | Yanıt | Neden |
|---|---|---|
| `400` | `{"error":"invalid_request","detail":"clientId and clientSecret are required."}` | Alanlardan biri boş veya yok. |
| `400` | `{"error":"invalid_request","detail":"The request body could not be read as JSON."}` | Gövde bozuk JSON. PowerShell'den `curl.exe` kullanırken tırnaklar bozulmuş olabilir (bkz. 5.5). |
| `401` | `{"error":"unauthorized"}` | Client yok, client secret yanlış veya client `disable` edilmiş. Hangisi olduğu güvenlik gereği söylenmez; sunucuda `minivault client list` ile bakılır. |
| `415` | `{"error":"invalid_request","detail":"Unsupported media type; send application/json."}` | `Content-Type: application/json` başlığı yok. |
| `429` | (gövde yok) | Aynı sunucuya bir dakikada 30'dan fazla token isteği geldi. Bir dakika bekleyin. Yalnızca bu endpoint sınırlıdır. |

**Postman'de:** Koleksiyondaki `Token` isteği. Body sekmesi **raw** + **JSON**, Authorization
**No Auth**. Post-response script token'ı `{{token}}` değişkenine yazar; bundan sonra diğer
istekleri sırayla gönderebilirsiniz. 15 dakika sonra `401` almaya başlarsanız bu isteği tekrar
gönderin.

### 3.3. Secret yazma: `PUT /v1/secrets/{name}`

**Ne işe yarar:** Verilen adla secret oluşturur; ad zaten varsa değerini değiştirir ve sürüm
numarasını bir artırır. Ayrı bir "oluştur" ve "güncelle" isteği yoktur. Token'ın rolünde bu adı
kapsayan **write** izni gerekir.

**İstek:**

```
PUT https://minivault.local:8200/v1/secrets/dataskope/collector/db-password
Authorization: Bearer eyJ...
Content-Type: application/json

{"value":"UEBzc3cwcmQtMjAyNg==","contentType":"text/plain"}
```

| Alan | Zorunlu | Anlamı |
|---|---|---|
| `value` | Evet | Secret değeri, base64. Çözülmüş hali en fazla 1.048.576 bayt (1 MB). `UEBzc3cwcmQtMjAyNg==` örneği `P@ssw0rd-2026` demektir. |
| `contentType` | Hayır | Serbest metin etiket, en fazla 128 karakter. Okuyan tarafa "bu bir metin mi, PFX mi" bilgisini taşır: `text/plain`, `application/x-pkcs12` gibi. Sunucu bu alanı yorumlamaz, aynen geri verir. Gönderilmezse `null` döner. |

**Yanıt (200):**

```json
{"version":1}
```

`version` bu ad için kaçıncı yazma olduğunu söyler: ilk yazmada `1`, her güncellemede bir artar.
Silinip yeniden yazılan secret tekrar `1`'den başlar.

**Hatalar:**

| Durum | Yanıt | Neden |
|---|---|---|
| `400` | `{"error":"invalid_request","detail":"Invalid secret name."}` | Ad, bölüm 1'deki kurallara uymuyor. |
| `400` | `{"error":"invalid_request","detail":"value is required (base64)."}` | `value` alanı yok. |
| `400` | `{"error":"invalid_request","detail":"Value must be base64-encoded."}` | `value` base64 değil. Düz metin yazılmış olabilir. |
| `400` | `{"error":"invalid_request","detail":"Secret value exceeds 1048576 bytes."}` | Değer 1 MB'den büyük. |
| `400` | `{"error":"invalid_request","detail":"contentType exceeds 128 characters."}` | `contentType` çok uzun. |
| `401` | `{"error":"unauthorized"}` | Token yok, süresi dolmuş veya bozuk. Yeni token alın. |
| `403` | `{"error":"forbidden"}` | Rolde bu adı kapsayan **write** izni yok. `read` izni yazmaya yetmez (bkz. 5.3). |
| `409` | `{"error":"conflict","detail":"The secret was modified concurrently; retry."}` | Aynı anda başka bir yazma oldu. İsteği tekrar gönderin. |

**Postman'de:** Koleksiyondaki `Secret - Write` isteği. URL `{{baseUrl}}/v1/secrets/{{secretName}}`,
Body **raw** + **JSON**. Değeri koleksiyon değişkeni `secretValuePlain` alanına düz metin olarak
yazın; pre-request script base64'e çevirir (bölüm 2.4). Postman `{{secretName}}` içindeki `/` karakterlerini olduğu gibi bırakır,
`%2F` yapmaz; ad yola doğrudan yazılır.

### 3.4. Secret okuma: `GET /v1/secrets/{name}`

**Ne işe yarar:** Secret'ın değerini ve üst bilgisini getirir. Token'ın rolünde bu adı kapsayan
**read** (veya write) izni gerekir.

**İstek:**

```
GET https://minivault.local:8200/v1/secrets/dataskope/collector/db-password
Authorization: Bearer eyJ...
```

**Yanıt (200):**

```
ETag: "1"

{"name":"dataskope/collector/db-password","value":"UEBzc3cwcmQtMjAyNg==","contentType":"text/plain","version":1,"updatedAt":"2026-09-04T09:15:30.1234567+00:00"}
```

| Alan | Anlamı |
|---|---|
| `name` | İstenen ad, aynen. |
| `value` | Değer, base64. Okumak için çözün (bölüm 2.4). |
| `contentType` | Yazan tarafın verdiği etiket veya `null`. |
| `version` | Güncel sürüm numarası. |
| `updatedAt` | Son yazma zamanı, UTC. |
| `ETag` başlığı | `"<version>"` biçiminde, koşullu okuma için (aşağıda). |

**Koşullu okuma (değişmediyse getirme):** Elinizdeki sürümü `If-None-Match` başlığıyla gönderirseniz
secret değişmemişse sunucu gövdesiz `304 Not Modified` döner. Client kütüphanesi arka plan
yenilemesinde bunu kullanır; Postman'den denemek için:

```
GET https://minivault.local:8200/v1/secrets/dataskope/collector/db-password
Authorization: Bearer eyJ...
If-None-Match: "1"
```

```
304 Not Modified
ETag: "1"
```

Değer arada yazılmışsa (`version` 2 olmuşsa) normal `200` ve yeni değer gelir. Tırnaklar başlığın
parçasıdır: `If-None-Match: "1"` doğru, `If-None-Match: 1` yanlıştır. `*` yazarsanız secret var
olduğu sürece her zaman `304` alırsınız.

**Hatalar:**

| Durum | Yanıt | Neden |
|---|---|---|
| `400` | `{"error":"invalid_request","detail":"Invalid secret name."}` | Ad kurallara uymuyor. |
| `401` | `{"error":"unauthorized"}` | Token yok, süresi dolmuş veya bozuk. |
| `403` | `{"error":"forbidden"}` | Rolde bu adı kapsayan izin yok. Secret'ın var olup olmadığı bu durumda söylenmez. |
| `404` | `{"error":"not_found"}` | İzin var ama bu adda secret yok. Adı ve büyük/küçük harfi kontrol edin. |

**Postman'de:** `Secret - Read` isteği. Post-response script'i değeri çözüp Postman **Console**'a
(alt çubukta **Console**) düz metin olarak yazar. `Secret - Read (If-None-Match)` isteği koşullu
okumayı dener; `etag` değişkenini son okumadan alır.

### 3.5. Secret listeleme: `GET /v1/secrets?prefix=`

**Ne işe yarar:** Verilen önekle başlayan secret'ların adlarını ve sürümlerini listeler. **Değerleri
getirmez**; değer için her biri ayrı ayrı okunur. Hangi secret'ların var olduğunu görmek veya bir
klasörü gezmek için kullanılır. Token'ın rolünde `prefix` değerini kapsayan **read** izni gerekir.

**İstek:**

```
GET https://minivault.local:8200/v1/secrets?prefix=dataskope/collector/
Authorization: Bearer eyJ...
```

**Yanıt (200):**

```json
[
  {"name":"dataskope/collector/api-key","version":3,"updatedAt":"2026-08-20T14:02:11+00:00"},
  {"name":"dataskope/collector/db-password","version":1,"updatedAt":"2026-09-04T09:15:30+00:00"}
]
```

Ada göre sıralı gelir. Eşleşen yoksa boş liste `[]` döner; bu bir hata değildir.

- `prefix` secret adının başlangıcıdır ve parça ortasında bitebilir: `dataskope/coll` de geçerlidir
  ve `dataskope/collector/...` ile `dataskope/collab/...` adlarını birlikte getirir. Bir klasörü
  istiyorsanız sonuna `/` koyun.
- `prefix` verilmezse veya boşsa **tüm kasa** istenmiş olur; bu yalnızca `--all` ile verilmiş
  (boş scope'lu) bir rolde çalışır, aksi halde `403` döner.
- İzin kontrolü `prefix` üzerinden yapılır: rolün scope'u `dataskope/collector/` ise
  `prefix=dataskope/` ile listeleme `403` alır, çünkü `dataskope/` altındaki her şeyi görme izni
  yoktur. Listeleyebildiğiniz en geniş önek rolünüzün scope'udur.

**Hatalar:**

| Durum | Yanıt | Neden |
|---|---|---|
| `400` | `{"error":"invalid_request","detail":"Invalid prefix."}` | `prefix` 256 karakterden uzun veya izin verilmeyen karakter içeriyor. |
| `401` | `{"error":"unauthorized"}` | Token yok, süresi dolmuş veya bozuk. |
| `403` | `{"error":"forbidden"}` | Rol, `prefix` kadar geniş bir alanı kapsamıyor. Öneki daraltın. |

**Postman'de:** `Secret - List` isteği. **Params** sekmesinde `prefix` anahtarı vardır; değerini
değiştirin veya tüm kasa için işaretini kaldırın.

### 3.6. Secret silme: `DELETE /v1/secrets/{name}`

**Ne işe yarar:** Secret'ı kalıcı olarak siler. Geri alınamaz; sürüm geçmişi tutulmaz. Token'ın
rolünde bu adı kapsayan **write** izni gerekir.

**İstek:**

```
DELETE https://minivault.local:8200/v1/secrets/dataskope/collector/db-password
Authorization: Bearer eyJ...
```

**Yanıt:** `204 No Content`, gövde yok.

**Hatalar:**

| Durum | Yanıt | Neden |
|---|---|---|
| `400` | `{"error":"invalid_request","detail":"Invalid secret name."}` | Ad kurallara uymuyor. |
| `401` | `{"error":"unauthorized"}` | Token yok, süresi dolmuş veya bozuk. |
| `403` | `{"error":"forbidden"}` | Rolde bu adı kapsayan **write** izni yok. |
| `404` | `{"error":"not_found"}` | Bu adda secret yok. İkinci kez silmek de `404` verir. |

**Postman'de:** `Secret - Delete` isteği. Silmeden önce `Secret - Read` ile doğru secret'a
baktığınızdan emin olun; adı yanlış yazarsanız `404` alırsınız, ama doğru yazıp yanlış secret'ı
silerseniz geri gelmez.

---

## 4. Hata yanıtları

### 4.1. HTTP durum kodları

| Durum | Anlamı | Ne yapmalı |
|---|---|---|
| `200` | Başarılı, gövdede yanıt var. | |
| `204` | Başarılı, gövde yok (silme). | |
| `304` | Secret değişmemiş (`If-None-Match` eşleşti). | Elinizdeki değeri kullanmaya devam edin. |
| `400` | İstek hatalı. `detail` alanı nedeni söyler. | Adı, gövdeyi, base64'ü düzeltin. |
| `401` | Kimlik yok veya geçersiz. | Token alın veya yenileyin; `/v1/auth/token`'da ise client id / secret'ı kontrol edin. |
| `403` | Kimlik geçerli ama izin yok. | Sunucuda `minivault role list` ile scope'u kontrol edin (bkz. 5.3). |
| `404` | Secret yok. | Adı ve büyük/küçük harfi kontrol edin. |
| `405` | Yol var ama bu method ile değil (örneğin `POST /v1/secrets/x`). | `PUT` kullanın. |
| `409` | Aynı anda başka bir yazma oldu. | İsteği tekrar gönderin. |
| `415` | Gövde `application/json` olarak işaretlenmemiş. | `Content-Type` başlığını ekleyin; Postman'de Body > raw > **JSON** seçin. |
| `429` | Token endpoint'ine dakikada 30'dan fazla istek. | Bir dakika bekleyin. |
| `500` | Beklenmeyen sunucu hatası. | Sunucu loguna bakın, ekibe bildirin. |
| `503` | Kasa geçici olarak kullanılamıyor: veritabanı veya master key ulaşılamaz. | `GET /v1/health` ile kontrol edin; `docs/saha-kilavuzu.md` bölüm 5.5. |

### 4.2. `error` kodları

Her hata gövdesinde `error` alanı aşağıdakilerden biridir. Programatik kontrol için bu alanı
kullanın; `detail` metni değişebilir.

| `error` | Anlamı |
|---|---|
| `invalid_request` | Girdi hatalı: secret adı, base64, eksik alan, bozuk JSON, yanlış method veya content type. `detail` neyin yanlış olduğunu söyler. |
| `unauthorized` | Token yok, süresi dolmuş, imzası tutmuyor; veya token endpoint'inde kimlik yanlış / client kapalı. |
| `forbidden` | Token geçerli ama rolde bu ad veya önek için gerekli izin yok. |
| `not_found` | Bu adda secret yok. |
| `conflict` | Eşzamanlı yazma çakışması; tekrar deneyin. |
| `vault_unavailable` | Veritabanı veya master key geçici olarak ulaşılamaz (`503`). |
| `internal_error` | Beklenmeyen hata (`500`). Sunucu loguna yazılmıştır. |

### 4.3. Audit kaydı

Endpoint'e ulaşan **her istek**, başarılı veya başarısız, veritabanındaki `AuditLog` tablosuna
yazılır: hangi client, hangi işlem (`token`, `secret.read`, `secret.write`, `secret.delete`,
`secret.list`), hangi secret, hangi IP'den, başarılı mı, değilse neden. Geçersiz token ile gelen
istekler endpoint'e ulaşmaz; bunlar `token.rejected` işlemi ve `(anonymous)` client id ile ayrıca
kaydedilir. Postman'den attığınız her deneme de bu tabloya düşer; test sunucusunda sorun değildir,
üretimde gereksiz deneme yapmayın.

---

## 5. Adım adım senaryolar

### 5.1. Postman'den ilk secret: yaz, oku, listele, sil

Ön koşul: sunucuda bir client ve write izinli bir rol açılmış olmalı
(`docs/saha-kilavuzu.md` bölüm 5.1). Örnek: `dataskope-collector` client'ı, scope'u
`dataskope/collector/` olan `collector-writer` rolü, permission `write`.

1. Environment'ı bölüm 2.2'deki gibi doldurun, koleksiyonu içe aktarın.
2. `Health` gönderin. `initialized: true` görmelisiniz.
3. `Token` gönderin. `200` ve `accessToken` gelmeli; environment'taki `token` dolmuş olmalı
   (environment'ın yanındaki göz simgesinden bakabilirsiniz).
4. `Secret - Write` gönderin. Yanıt `{"version":1}`.
5. `Secret - Read` gönderin. Yanıtta aynı `value` ve `version: 1`; Console'da çözülmüş düz metin.
6. `Secret - Write`'ı değeri değiştirip tekrar gönderin. Yanıt `{"version":2}`.
7. `Secret - List` gönderin. Listede secret `version: 2` ile görünmeli.
8. `Secret - Delete` gönderin. `204`. Ardından `Secret - Read` artık `404` verir.

### 5.2. Bir servisin gördüğü değeri doğrulama

Bir servis "yanlış parola" diyorsa MiniVault'ta ne yazdığını görmek için:

1. O servisin **kendi** client id ve secret'ıyla değil, test için açılmış bir client ile token alın.
   Servisin client secret'ı bir daha gösterilemez; sunucudan `client add` ile yeni bir test client'ı
   açıp aynı rolü verin.
2. `Secret - Read` ile secret'ı okuyun, Console'daki çözülmüş değere bakın.
3. Değer doğruysa sorun serviste (önbellek, yanlış ad, yanlış ortam). Yanlışsa `Secret - Write` ile
   düzeltin; servis en geç bir sonraki yenilemesinde yeni değeri alır.
4. İş bitince test client'ını sunucuda `minivault client remove` ile silin.

### 5.3. `403 forbidden` alıyorum

`403`, token'ın geçerli olduğunu ama izin olmadığını söyler. Sırayla:

1. İstediğiniz işlem **write** mi (`PUT`, `DELETE`)? Rolün permission'ı `read` ise `403` alırsınız;
   sunucuda `minivault role grant <rol> --scope <scope> --permission write` gerekir.
2. Secret adı rolün scope'uyla **aynı harflerle** başlıyor mu? Scope `dataskope/collector/` ise ad
   `dataskope/collector/db-password` olmalı; `Dataskope/...` veya `dataskope/collector-x/...` `403`
   verir.
3. Listelemede `prefix`, scope'tan daha geniş mi? `prefix=dataskope/` ile scope
   `dataskope/collector/` olan rol `403` alır. Öneki scope'a eşitleyin.
4. İzin sunucuda az önce verildiyse elinizdeki token eski rolleri taşır. `Token` isteğini tekrar
   gönderip yeni token alın; roller token'ın içindedir.

### 5.4. `401 unauthorized` alıyorum

- **Token endpoint'inde:** `clientId` / `clientSecret` yanlış veya client `disable` edilmiş.
  Sunucuda `minivault client list` ile client'ın `[enabled]` olduğunu görün. Secret şüpheliyse
  client silinip yeniden açılır (`docs/saha-kilavuzu.md` bölüm 5.2).
- **Diğer endpoint'lerde:** Token süresi dolmuş (15 dakika) veya hiç gönderilmemiş. `Token`
  isteğini tekrar gönderin. Postman'de isteğin **Authorization** sekmesinin **Inherit auth from
  parent** olduğunu ve environment'ın seçili olduğunu kontrol edin; environment seçili değilse
  `{{token}}` boş gider.
- Sunucu yeniden kurulduysa veya `minivault recover` çalıştıysa eski token'lar geçersizdir; yeni
  token alın.

### 5.5. Postman yerine `curl.exe` ile aynı istekler

Sunucunun kendisinden veya Postman olmayan bir makineden. PowerShell 5.1'de `curl` yerine mutlaka
`curl.exe`; JSON gövdeyi doğrudan komut satırına değil **dosyaya** yazıp `-d "@dosya"` ile verin,
aksi halde tırnaklar bozulur ve `invalid_request` alırsınız. `-k` yalnızca test sertifikası içindir.

```powershell
$base = "https://minivault.local:8200"

curl.exe -sk "$base/v1/health"

Set-Content token.json -Encoding ascii -Value '{"clientId":"dataskope-collector","clientSecret":"<client secret>"}'
$token = (curl.exe -sk -X POST "$base/v1/auth/token" -H "Content-Type: application/json" -d "@token.json" | ConvertFrom-Json).accessToken

Set-Content value.json -Encoding ascii -Value '{"value":"UEBzc3cwcmQtMjAyNg==","contentType":"text/plain"}'
curl.exe -sk -X PUT "$base/v1/secrets/dataskope/collector/db-password" -H "Authorization: Bearer $token" -H "Content-Type: application/json" -d "@value.json"

curl.exe -sk "$base/v1/secrets/dataskope/collector/db-password" -H "Authorization: Bearer $token"

curl.exe -sk "$base/v1/secrets?prefix=dataskope/collector/" -H "Authorization: Bearer $token"

curl.exe -sk -X DELETE "$base/v1/secrets/dataskope/collector/db-password" -H "Authorization: Bearer $token" -i

Remove-Item token.json, value.json
```

`token.json` client secret'ı içerir; işiniz bitince silin. Başlıkları da görmek için (`ETag`,
`304`) komuta `-i` ekleyin.

---

## 6. Sık sorulanlar

**Değeri neden base64 gönderiyorum, düz metin yazamaz mıyım?**
Hayır. `value` alanı düz metin olursa `Value must be base64-encoded.` hatası alırsınız. JSON yalnızca
metin taşıyabildiğinden ve aynı alan PFX gibi ikili değerleri de taşıdığından tek biçim base64'tür.
Ayrıntılı açıklama bölüm 1.1'de, çevirme yolları bölüm 2.4'te.

**Secret'ın eski sürümünü okuyabilir miyim?**
Hayır. Yalnızca güncel değer saklanır; `version` sadece kaç kez yazıldığını sayar. Üzerine
yazılan değer geri gelmez.

**Bir secret'ı yeniden adlandırabilir miyim?**
Doğrudan hayır. Eski adı okuyup yeni ada yazın, sonra eskisini silin. Rolün her iki adı da
kapsadığından emin olun.

**Token'ı başka bir client için kullanabilir miyim?**
Hayır. Token içinde client id ve o anki roller yazılıdır ve imzalıdır. Rol değişikliği için yeni
token alınır.

**Postman'den aynı anda birden fazla kişi bağlanabilir mi?**
Evet. Tek sınır token endpoint'indeki dakikada 30 istektir; token aldıktan sonraki isteklerde sınır
yoktur.

**Yanıt `502` veya bağlantı hatası, MiniVault'tan değil gibi.**
MiniVault `502` üretmez. Arada bir proxy veya yük dengeleyici vardır ya da sunucu hiç ayakta
değildir. Önce sunucunun kendisinden `curl.exe -k https://localhost:8200/v1/health` deneyin.

**Postman "Could not get any response" diyor.**
Sırayla: adres `https://` ile mi başlıyor; port `8200` mü; Settings > General > SSL certificate
verification kapalı mı (test sertifikası için); güvenlik duvarı 8200'ü açıyor mu.

**Denemelerim bir yerde iz bırakıyor mu?**
Evet, her istek `AuditLog` tablosuna client id, IP ve zamanla yazılır (bölüm 4.3). Test sunucusunda
sorun değildir.

---

## 7. Hızlı referans

| İş | İstek | Token | Başarı |
|---|---|---|---|
| Sağlık | `GET /v1/health` | Hayır | `200` `{"status","initialized","activeDataKeyVersion"}` |
| Token al | `POST /v1/auth/token` gövde `{"clientId","clientSecret"}` | Hayır | `200` `{"accessToken","expiresIn"}` |
| Secret yaz | `PUT /v1/secrets/{name}` gövde `{"value":"<base64>","contentType"}` | write | `200` `{"version"}` |
| Secret oku | `GET /v1/secrets/{name}` | read | `200` `{"name","value","contentType","version","updatedAt"}` + `ETag` |
| Değiştiyse oku | `GET /v1/secrets/{name}` başlık `If-None-Match: "<version>"` | read | `304` veya `200` |
| Listele | `GET /v1/secrets?prefix=<önek>` | read (önek için) | `200` `[{"name","version","updatedAt"}]` |
| Sil | `DELETE /v1/secrets/{name}` | write | `204` |

Ortak: adres `https://<sunucu>:8200`, gövdeli isteklerde `Content-Type: application/json`, token
isteyenlerde `Authorization: Bearer <token>`, hata gövdesi `{"error","detail"}`. Token 15 dakika
geçerlidir; `401` alınca yenisi alınır.
