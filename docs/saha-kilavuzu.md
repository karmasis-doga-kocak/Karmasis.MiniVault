# MiniVault Saha Kılavuzu

Bu kılavuz, MiniVault kurulu bir sunucuda günlük yönetim işlerini yapacak saha ekibi içindir.
Kriptografi bilgisi gerektirmez. Her komut tam haliyle, örnek çıktısıyla ve "ne zaman kullanılır"
açıklamasıyla verilmiştir. Daha derin teknik detay için `docs/operations.md` (İngilizce) ve
`docs/design.md` (Türkçe tasarım) dosyalarına bakılır.

---

## 1. Beş dakikada MiniVault

**MiniVault nedir?** Karmasis servislerinin parola, bağlantı bilgisi, sertifika parolası gibi
gizli değerlerini ("secret") saklayan küçük bir kasadır. Servisler bu değerleri config dosyasına
yazmak yerine MiniVault'tan alır.

**Kavramlar** (bunları bilmek yeterlidir):

| Kavram | Ne demek | Benzetme |
|---|---|---|
| **Secret** | Kasada saklanan gizli değer. Adı vardır, örneğin `dataskope/collector/db-password`. | Kasadaki bir zarf |
| **Client** | MiniVault'a bağlanan bir servis. Bir kimliği (`client id`) ve bir parolası (`client secret`) vardır. | Kasaya erişebilen bir çalışan |
| **Client secret** | Client'ın parolası. Sadece oluşturulduğu anda bir kez gösterilir, bir daha gösterilemez. | Çalışanın kartı |
| **Role** | Hangi secret'lara erişilebileceğini söyleyen izin listesi. Client'lara rol atanır. | Hangi çekmecelere erişim var |
| **Scope** | Rolün kapsadığı secret adlarının başlangıcı. `dataskope/collector/` scope'u, bu adla başlayan tüm secret'ları kapsar. | Çekmece etiketi |
| **Master key** | Kasanın ana anahtarı. Sunucuda otomatik saklanır, elle dokunulmaz. | Kasa anahtarı |
| **Recovery key** | Master key kaybolursa kasayı açan yedek anahtar. Kurulumda bir kez gösterilir, dışarıda güvenle saklanır. | Kasa anahtarının kasadaki yedeği |

**Akış:** Servis, `client id` + `client secret` ile MiniVault'a giriş yapar, 15 dakikalık bir
geçici bilet (token) alır ve bu biletle secret'larını okur. MiniVault kapalıyken servisler daha
önce okudukları secret'ları kendi yerel önbelleklerinden kullanmaya devam eder, durmazlar.

---

## 2. Başlamadan önce

**Komutlar nerede çalıştırılır?** Tüm komutlar MiniVault'un kurulu olduğu sunucuda, **yönetici
olarak açılmış** bir PowerShell penceresinde çalıştırılır. Yönetici olmayan bir pencerede komutlar
"erişim reddedildi" benzeri hatalar verir.

1. Başlat menüsünde "PowerShell" yazın.
2. "Windows PowerShell" üzerine sağ tıklayın, **Yönetici olarak çalıştır** seçin.
3. Kurulum dizinine geçin:

```powershell
cd "C:\Program Files\Karmasis\MiniVault"
```

Bu dizinde `minivault.exe` bulunur. Aşağıdaki tüm komutlar `.\minivault.exe ...` veya kısaca
`.\minivault ...` şeklinde çalıştırılır. Kılavuzda kısalık için başındaki `.\` yazılmamıştır.

**Servis bilgileri:**

| Bilgi | Değer |
|---|---|
| Windows servis adı | `KarmasisMiniVault` |
| Kurulum dizini (varsayılan) | `C:\Program Files\Karmasis\MiniVault` |
| Ayar ve anahtar dosyaları | `C:\ProgramData\MiniVault` |
| Adres (varsayılan) | `https://<sunucu>:8200` |

**Servis çalışıyor mu?**

```powershell
Get-Service KarmasisMiniVault
```

```
Status   Name               DisplayName
------   ----               -----------
Running  KarmasisMiniVault  Karmasis MiniVault
```

**Sağlık kontrolü** (sunucunun kendisinden):

```powershell
curl.exe -k https://localhost:8200/v1/health
```

```
{"status":"ok","initialized":true,...}
```

`initialized` değeri `true` ise kasa hazırdır. PowerShell'de `curl` yerine mutlaka `curl.exe`
yazın; `curl` tek başına farklı bir komuttur.

**Servisi yeniden başlatma** (bazı komutlardan sonra gerekir):

```powershell
Restart-Service KarmasisMiniVault
```

---

## 3. Günlük komutlar: rol ve client yönetimi

Bu bölümdeki komutlar en sık kullanılanlardır ve güvenlidir; yanlış yapılırsa geri alınabilir.

### 3.1. Rol oluşturma

**Ne işe yarar:** Bir izin grubu tanımlar. Önce rol, sonra o role izinler, en son client eklenir.

```
minivault role add collector-reader --description "Collector servisinin okuma izni"
```

```
Role created: collector-reader
```

- Rol adı: harf, rakam, nokta, tire, alt çizgi. Boşluk kullanmayın.
- `--description` isteğe bağlıdır ama ne için açıldığını yazmak sonradan işe yarar.

### 3.2. Role izin verme

**Ne işe yarar:** Rolün hangi secret'lara ulaşabileceğini belirler.

```
minivault role grant collector-reader --scope dataskope/collector/ --permission read
```

```
Granted Read on 'dataskope/collector/' to collector-reader
```

- `--scope`: Secret adının başlangıcı. **Sonuna `/` koyun.** `dataskope/collector/` yazarsanız
  yalnızca bu klasör altındakiler kapsanır; `/` koymazsanız `dataskope/collector-eski/...` gibi
  adlar da yanlışlıkla kapsanır.
- `--permission`: `read` (sadece okuma) veya `write` (okuma + yazma + silme).
- Aynı scope'a ikinci kez izin verirseniz eskisi değişir, ikinci bir satır eklenmez.
- Bir role birden fazla scope verilebilir; komutu her scope için tekrarlayın.

**Tüm kasaya erişim** (yalnızca acil durum rolü için, dikkatli kullanın):

```
minivault role grant break-glass --all --permission write
```

### 3.3. Rolleri listeleme

```
minivault role list
```

```
collector-reader: dataskope/collector/=Read
break-glass: =Write
empty-role: (no rules)
```

`(no rules)` görünen rol hiçbir şeye erişim vermez. Scope kısmı boş görünen satır (`=Write`) tüm
kasaya erişim demektir.

### 3.4. Rol silme

**Ne işe yarar:** Rolü, izinlerini ve bu rolün client'lara atamalarını siler.

```
minivault role remove collector-reader
```

```
Role removed: collector-reader
```

Silinen rolü kullanan client'lar o andan itibaren bu role ait secret'lara erişemez.

### 3.5. Client oluşturma (en önemli komut)

**Ne işe yarar:** Bir servise MiniVault kimliği verir ve parolasını (client secret) üretir.

```
minivault client add dataskope-collector --role collector-reader
```

```
Client created: dataskope-collector
Client secret: 8k3F2v9qA1zR7pC0eQ6nS4gU2y5T0hJ3W8lD1bXfM6o=
Store this secret now; it is not shown again.
```

**ÇOK ÖNEMLİ:**

- `Client secret` satırındaki değer **yalnızca bu anda** görünür. MiniVault bu değeri saklamaz
  ve bir daha gösteremez.
- Değeri hemen kopyalayın ve servisin kurulum adımına göre yerine koyun. Servisin kendi kurulum
  kılavuzu bunun nereye yazılacağını söyler; Windows'ta bu değer DPAPI ile korunmuş bir dosyaya
  konur, düz metin olarak config dosyasına yazılmaz.
- Kaybolursa yeni bir client secret üretmenin tek yolu client'ı silip yeniden eklemektir
  (bkz. senaryo 5.2).
- Client id kuralı: harf, rakam, nokta, tire, alt çizgi; boşluk yok. Servisin adını kullanın,
  örneğin `dataskope-collector`, `infraskope-webservice`.
- `--role` birden fazla kez yazılabilir: `--role okuma --role yazma`.

### 3.6. Client'a rol ekleme

**Ne işe yarar:** Var olan bir client'a sonradan rol verir.

```
minivault client assign dataskope-collector --role collector-reader
```

```
Assigned role collector-reader to dataskope-collector
```

Zaten atanmış bir rolü tekrar atamak hata vermez, bir şey de değiştirmez.

### 3.7. Client'ı geçici kapatma ve açma

**Ne işe yarar:** Bir client'ı silmeden erişimini keser. Şüpheli bir durumda ilk yapılacak iştir.

```
minivault client disable dataskope-collector
```

```
Client disabled: dataskope-collector
```

```
minivault client enable dataskope-collector
```

```
Client enabled: dataskope-collector
```

Kapatılan client yeni giriş yapamaz. Elinde geçerli bir bilet (token) varsa en fazla 15 dakika
daha çalışır, sonra tamamen kesilir.

### 3.8. Client silme

**Ne işe yarar:** Client'ı kalıcı olarak siler. Client secret'ı kaybolduğunda veya servis
tamamen kaldırıldığında kullanılır.

```
minivault client remove dataskope-collector
```

```
Client removed: dataskope-collector
```

Silme geri alınamaz; aynı adla yeniden eklenirse yeni bir client secret üretilir ve roller
yeniden atanmalıdır.

### 3.9. Client'ları listeleme

```
minivault client list
```

```
dataskope-collector [enabled]: collector-reader
infraskope-webservice [enabled]: infraskope-reader, infraskope-writer
eski-servis [disabled]: (no roles)
```

Bir bakışta hangi servisin açık/kapalı olduğu ve hangi rolleri taşıdığı görülür.

---

## 4. Nadir komutlar: kurulum, anahtar ve bakım

Bu komutlar kasanın kendisini etkiler. Normal günlük işte kullanılmazlar. **Emin değilseniz
çalıştırmadan önce sorumlu kişiye danışın.**

### 4.1. Kasayı ilk kez oluşturma

**Ne zaman:** Yalnızca yeni kurulumda, bir kez. MSI veya `install.ps1` ile kurulum yapıldıysa bu
adım zaten otomatik yapılmıştır; tekrar çalıştırmayın. Zaten oluşturulmuş bir kasada komut
çalışmayı reddeder.

Tek recovery key ile:

```
minivault init --recovery single
```

```
MiniVault initialized.
Recovery mode: single

Store the following recovery material offline, in separate places. It is shown only once and is not saved anywhere.
Recovery key: AQ...

Master key stored by the Dpapi provider.
```

Parçalı recovery key ile (3 parça üretilir, herhangi 2'si kasayı açar):

```
minivault init --recovery shamir --shares 3 --threshold 2
```

```
MiniVault initialized.
Recovery mode: shamir (2 of 3)

Store the following recovery material offline, in separate places. It is shown only once and is not saved anywhere.
Share 1: AQ...
Share 2: Ag...
Share 3: Aw...

Master key stored by the Dpapi provider.
```

Çıktıyı ayrıca bir dosyaya yazdırmak için:

```
minivault init --recovery shamir --shares 3 --threshold 2 --out C:\Temp\recovery.txt
```

**ÇOK ÖNEMLİ:**

- `Recovery key` veya `Share` satırları **bir kez** gösterilir ve hiçbir yere kaydedilmez.
- Bunları sunucu dışında güvenli bir yerde saklayın (parola kasası, kilitli ortam). Parçalı
  modda her parçayı ayrı kişiye verin.
- Bu değerler kaybolur ve master key de kaybolursa kasadaki secret'lar bir daha okunamaz.
- `--out` ile yazılan dosyayı güvenli yere aldıktan sonra sunucudan silin.

### 4.2. Master key'i değiştirme veya kurtarma

**Ne zaman:** Sunucu değişti, `C:\ProgramData\MiniVault\masterkey.bin` silindi veya servis
"Master key unavailable" hatasıyla başlamıyorsa. Ayrıca master key'i düzenli değiştirmek
istendiğinde.

Tek recovery key ile:

```
minivault recover --new-master-key auto --recovery-key <kurulumda verilen recovery key>
```

Parçalı recovery key ile (threshold kadar parça yeterlidir, sırası önemsizdir):

```
minivault recover --new-master-key auto --share <parça 1> --share <parça 3>
```

```
Master key replaced. Data keys rewrapped: 2.
Master key stored by the Dpapi provider.
```

- `--new-master-key auto` yeni master key'i rastgele üretir ve sunucuda saklar. Bu tercih edilen
  kullanımdır.
- Secret'lara dokunulmaz, hepsi aynen kalır.
- Recovery key değişmez, aynı değerler sonraki kurtarmada da geçerlidir.
- Komuttan sonra servisi yeniden başlatın: `Restart-Service KarmasisMiniVault`.

### 4.3. Data key yenileme

**Ne zaman:** Güvenlik politikası gereği belirli aralıklarla (örneğin yılda bir) veya sürüm
yükseltme sonrasında istenirse. Günlük işte gerekmez.

```
minivault rotate-dek
```

```
Active data key version: 2
```

Bundan sonra yazılan secret'lar yeni anahtarla şifrelenir; eskiler okunmaya devam eder.
**Komuttan sonra servisi yeniden başlatın**, aksi halde çalışan servis yeni anahtarı görmez.

### 4.4. Sürüm yükseltme sonrası veritabanını güncelleme

**Ne zaman:** MiniVault'un yeni sürümü kurulduktan sonra, servisi başlatmadan önce.

```
minivault migrate
```

```
Applied 1 migration(s).
```

veya yapılacak bir şey yoksa:

```
Database is up to date.
```

Birden fazla kez çalıştırmak zararsızdır.

### 4.5. Veritabanı bağlantı bilgisini şifreli hale getirme

**Ne zaman:** `appsettings.json` içindeki veritabanı bağlantı satırı elle değiştirilecekse.
Kurulum bunu otomatik yapar; yalnızca elle düzenleme veya başka sunucuya taşıma durumunda gerekir.

```
minivault protect --connection-string "Server=sql01;Database=MiniVault;User ID=minivault_svc;Password='parola'"
```

```
AQAAANCMnd8BFdERjHoAwE/Cl+sBAAAA...
```

Çıkan uzun değer `C:\ProgramData\MiniVault\appsettings.json` dosyasına şu şekilde yazılır:

```json
"ConnectionStrings": {
  "MiniVault": null,
  "MiniVaultProtected": "AQAAANCMnd8BFdERjHoAwE/Cl+sBAAAA..."
}
```

- Bu değer yalnızca üretildiği sunucuda çalışır. Başka sunucuya kopyalanamaz; orada yeniden
  üretilmelidir.
- Parola komut satırında göründüğü için komutu bitirdikten sonra PowerShell geçmişini temizleyin:
  `Clear-History`.
- Sonrasında servisi yeniden başlatın.

### 4.6. Sunucuyu elle başlatma

Normalde sunucu Windows servisi olarak çalışır ve buna gerek yoktur. Sorun ararken servisi
durdurup pencerede çalıştırmak hatayı doğrudan görmenizi sağlar:

```powershell
Stop-Service KarmasisMiniVault
.\minivault.exe
```

Hata mesajı doğrudan ekranda görünür. Bitirince `Ctrl+C` ile kapatıp servisi tekrar başlatın:

```powershell
Start-Service KarmasisMiniVault
```

---

## 5. Adım adım senaryolar

### 5.1. Yeni bir servisi MiniVault'a bağlama

Örnek: `DataskopeCollector` servisi `dataskope/collector/` altındaki secret'ları okuyacak.

```
minivault role add collector-reader --description "Collector servisi okuma"
minivault role grant collector-reader --scope dataskope/collector/ --permission read
minivault client add dataskope-collector --role collector-reader
```

1. Son komutun bastığı `Client secret` değerini hemen kopyalayın.
2. Servisin kurulum adımına göre `client id` (`dataskope-collector`) ve `client secret` değerini
   servise tanıtın.
3. Servisi başlatın ve MiniVault'a bağlandığını servisin kendi logundan doğrulayın.
4. Kontrol: `minivault client list` çıktısında client `[enabled]` ve rolü doğru görünmeli.

### 5.2. Client secret kayboldu veya sızdı

Client secret geri getirilemez; yenisi üretilir.

```
minivault client remove dataskope-collector
minivault client add dataskope-collector --role collector-reader
```

1. Yeni `Client secret` değerini kopyalayın ve serviste eskisinin yerine koyun.
2. Servisi yeniden başlatın.
3. Servis, eski secret ile şifrelenmiş yerel önbelleğini açamaz; bu normaldir, ilk bağlantıda
   önbelleği yeniden oluşturur.

Sızma şüphesi varsa önce `disable` ile hemen kesin, sonra sakin kafayla yukarıdaki adımları yapın:

```
minivault client disable dataskope-collector
```

### 5.3. Bir servis "yetkisiz" (403) hatası alıyor

1. `minivault client list` ile client'ın `[enabled]` olduğunu ve beklenen rolü taşıdığını kontrol
   edin.
2. `minivault role list` ile rolün scope'unun secret adıyla eşleştiğini kontrol edin. Secret adı
   `dataskope/collector/db-password` ise scope `dataskope/collector/` olmalıdır. Büyük/küçük harf
   önemlidir.
3. Eksik izni `minivault role grant ...` ile verin. Servisi yeniden başlatmak gerekmez, en geç
   15 dakika içinde yeni bilet alırken izin devreye girer; hemen etkisi için servisi yeniden
   başlatın.

### 5.4. Bir servis "giriş hatası" (401) alıyor

1. `minivault client list` ile client'ın var ve `[enabled]` olduğunu kontrol edin.
2. Servisteki `client id` ve `client secret` değerlerinin doğru olduğundan emin olun. Secret
   şüpheliyse senaryo 5.2'yi uygulayın.

### 5.5. MiniVault servisi başlamıyor

1. Windows Olay Görüntüleyicisi > Windows Logs > Application altında `MiniVault cannot start:`
   ile başlayan satırı bulun. Neden bu satırda yazar.
2. Sık nedenler:

| Mesaj | Ne yapmalı |
|---|---|
| `The vault is not initialized` | Kasa hiç oluşturulmamış. `minivault init ...` (bölüm 4.1). Kurulum yapıldıysa bağlantı bilgisinin doğru veritabanına baktığını kontrol edin. |
| `Master key unavailable` | `masterkey.bin` yok veya başka sunucudan kopyalanmış. `minivault recover ...` (bölüm 4.2). |
| `Database is not reachable` | SQL sunucusuna ulaşılamıyor. Bağlantı bilgisini ve SQL servisini kontrol edin. |
| `Could not load the TLS certificate` | Sertifika dosyası veya parolası yanlış. `appsettings.json` içindeki `Tls` bölümünü kontrol edin. |

3. Nedeni bulamazsanız bölüm 4.6'daki gibi elle çalıştırıp hatayı ekranda görün.

### 5.6. Sürüm yükseltme

```powershell
Stop-Service KarmasisMiniVault
```

Yeni sürümü kurun (MSI veya dosya kopyalama), ardından:

```
minivault migrate
```

```powershell
Start-Service KarmasisMiniVault
curl.exe -k https://localhost:8200/v1/health
```

### 5.7. MiniVault kapalıyken servisler ne olur?

Servisler durmaz. Her servis daha önce okuduğu secret'ları kendi makinesinde şifreli bir
önbellekte tutar ve MiniVault'a ulaşamadığında oradan okur. Yalnızca şu durumlarda sorun çıkar:

- Servis daha önce hiç okumadığı yeni bir secret istiyorsa.
- Servisin önbellek dizini ayarlanmamışsa ve servis MiniVault kapalıyken yeniden başlatıldıysa.

MiniVault geri geldiğinde servisler kendiliğinden tekrar bağlanır; yeniden başlatmak gerekmez.

---

## 6. Sık sorulanlar

**Client secret'ı tekrar görebilir miyim?**
Hayır. Kaybolduysa senaryo 5.2 ile yenisi üretilir.

**Recovery key'i tekrar görebilir miyim veya yenisini üretebilir miyim?**
Hayır. Bu sürümde recovery key yalnızca kurulumda üretilir; sonradan gösteren veya yenileyen bir
komut yoktur. Recovery key kaybolduysa master key sağlam olduğu sürece kasa çalışmaya devam eder,
ancak master key de kaybolursa secret'lar bir daha okunamaz. Böyle bir durumda sorumlu kişiye
haber verin; çözüm yeni bir kasa kurup secret'ları yeniden girmektir.

**Secret'ları komut satırından ekleyebilir miyim?**
Hayır. Secret'lar yalnızca yetkili servisler tarafından, MiniVault'un web adresi üzerinden yazılır
ve okunur. Komut satırı yalnızca client ve rol yönetimi içindir.

**Bir komut "erişim reddedildi" veya veritabanı hatası veriyor.**
PowerShell'i yönetici olarak açtığınızdan emin olun (bölüm 2).

**`curl` komutu garip çıktı veriyor.**
PowerShell'de `curl` başka bir komutun kısaltmasıdır. Her zaman `curl.exe` yazın.

**Komutları başka bir makineden çalıştırabilir miyim?**
Hayır. Komutlar MiniVault sunucusunun üzerinde çalışır; ayar ve anahtar dosyaları o makinededir.

**Bir komut yanlışlıkla çalıştı, nasıl anlarım?**
Her komut (listeleme hariç) veritabanındaki `AuditLog` tablosuna `cli` kimliğiyle kayıt düşer.
Ne zaman, hangi komutun çalıştığı buradan görülür.

---

## 7. Hızlı referans

| İş | Komut |
|---|---|
| Servis durumu | `Get-Service KarmasisMiniVault` |
| Servisi yeniden başlat | `Restart-Service KarmasisMiniVault` |
| Sağlık kontrolü | `curl.exe -k https://localhost:8200/v1/health` |
| Rol ekle | `minivault role add <rol> --description "..."` |
| Role izin ver | `minivault role grant <rol> --scope <yol/> --permission read\|write` |
| Rolleri listele | `minivault role list` |
| Rol sil | `minivault role remove <rol>` |
| Client ekle (secret üretir) | `minivault client add <id> --role <rol>` |
| Client'a rol ata | `minivault client assign <id> --role <rol>` |
| Client kapat / aç | `minivault client disable <id>` / `minivault client enable <id>` |
| Client sil | `minivault client remove <id>` |
| Client'ları listele | `minivault client list` |
| Kasayı ilk kez oluştur | `minivault init --recovery single` |
| Master key kurtar | `minivault recover --new-master-key auto --recovery-key <key>` |
| Data key yenile (sonra restart) | `minivault rotate-dek` |
| Yükseltme sonrası DB güncelle | `minivault migrate` |
| Bağlantı bilgisini şifrele | `minivault protect --connection-string "..."` |

Tüm komutlar yönetici PowerShell'de, `C:\Program Files\Karmasis\MiniVault` dizininde,
`.\minivault ...` şeklinde çalıştırılır.
