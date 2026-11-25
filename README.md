# DCBypass

**DCBypass**, Windows üzerinde herhangi bir çekirdek sürücüsü (kernel driver / WinDivert) veya VPN kullanmadan, yalnızca belirlenen alan adları için yerel proxy üzerinden **DNS-over-HTTPS (DoH)** ve **TLS Record Split** uygulayan hafif ve açık kaynaklı bir ağ aracıdır.

---

## Ne İşe Yarar?

Bazı internet servis sağlayıcıları (ISS), belirli alan adlarına erişimi DNS yönlendirmesi ve Derin Paket İnceleme (DPI) yöntemleriyle kısıtlar. **DCBypass**:

1. Bilgisayarınızda yalnızca yerel ağ arayüzünde (`127.0.0.1`, otomatik seçilen boş bir portta) çalışan bir HTTP/HTTPS proxy başlatır.
2. Yalnızca tanımlı alan adlarına (`discord.com`, `discordapp.com`, `discordapp.net`, `discord.gg`, `discord.media`, `discordcdn.com`, `discordstatus.com`, `dis.gd`) giden bağlantılarda:
   - Alan adı çözümlemesini ISS DNS'i yerine **Cloudflare DoH (`1.1.1.1`)** üzerinden şifreli olarak yapar.
   - İlk TLS el sıkışma paketini (ClientHello) standart TLS protokolüne uygun biçimde iki küçük TLS kaydına böler (**TLS Record Split**).
3. Diğer tüm web sitelerini ve bağlantıları herhangi bir müdahale yapmadan doğrudan iletir.

---

## Neyi Nasıl Etkiler? (Ve Neyi Etkilemez?)

| Alan | Etki | Açıklama |
|---|---|---|
| **Windows Proxy Ayarı** | Geçici olarak açılır | DCBypass çalışırken `HKCU\...\Internet Settings` altındaki sistem proxy'si yerel boş porta (`127.0.0.1:<port>`) ayarlanır. Uygulamadan **Çıkış** yapıldığında veya bilgisayar kapanırken **otomatik olarak eski haline döndürülür**. |
| **İnternet Hızı ve Ping** | Etkilemez | Trafiğiniz başka bir ülke veya VPN sunucusundan dolaşmaz; doğrudan hedef sunucuya gider. |
| **Oyunlar** | Etkilemez | Çevrimiçi oyunlar Windows HTTP proxy ayarını kullanmaz; doğrudan normal internet bağlantınızdan çalışır. |
| **Diğer Web Siteleri** | Değiştirilmez | Tanımlı alan adları dışındaki hiçbir siteye DPI bölme veya DoH uygulanmaz; normal DNS ve standart bağlantı kullanılır. |
| **Windows Defender / Güvenlik** | Sürücü kurmaz | Çekirdek sürücüsü (`WinDivert` vb.) içermez, yönetici izni gerektirmez ve sistem dosyalarını değiştirmez. |
| **Gizlilik ve Veri** | Sıfır telemetri | Uygulama tamamen yerel çalışır; herhangi bir sunucuya kullanıcı verisi, log veya istatistik göndermez. |

---

## Kullanım

1. `DCBypass.exe` dosyasına çift tıklayın.
2. Siyah komut penceresi açılmaz; uygulama sağ altta **sistem tepsisinde (System Tray)** yeşil simgeyle çalışmaya başlar.
3. Discord'u kendi normal kısayolundan açabilir veya tepsi simgesine sağ tıklayıp **Discord'u Başlat / Kapat** seçeneğini kullanabilirsiniz.
4. İşiniz bittiğinde simgeye sağ tıklayıp **Çıkış** deyin — Windows proxy ayarı otomatik olarak eski haline döner.

### Sağ Tık Menüsü

- **Proxy'yi Durdur / Başlat:** Sistem proxy'sini ve yerel sunucuyu tek tıkla duraklatır veya yeniden açar (simgeye çift tıklayarak da aynı işlem yapılabilir).
- **Discord'u Başlat / Kapat:** Discord'un açık olup olmadığını algılar ve duruma göre başlatır veya kapatır.
- **Çıkış:** Proxy ayarını eski haline getirir ve uygulamayı tamamen kapatır.

---

## Kaynak Koddan Derleme

Gereksinim: **.NET 8 SDK** (Windows)

```powershell
dotnet publish DCBypass -c Release -r win-x64 --no-self-contained -p:PublishSingleFile=true -p:DebugType=None -o publish
```

Çıktı olarak `publish\DCBypass.exe` oluşur.

---

## Yasal Uyarı ve Sorumluluk Reddi

> [!IMPORTANT]
> Bu yazılım yalnızca **eğitim, araştırma, ağ protokollerinin incelenmesi ve bireysel kullanım** amacıyla geliştirilmiş açık kaynaklı bir ağ aracıdır.

1. **Kullanıcı Sorumluluğu:** Bu yazılımı indirmek, derlemek, çalıştırmak veya kullanmak tamamen kullanıcının kendi özgür iradesine ve tercihine bağlıdır. Bulunduğunuz ülkenin yürürlükteki yasalarına, yönetmeliklerine ve internet servis sağlayıcınızın kullanım koşullarına uygun hareket etme sorumluluğu **münhasıran kullanıcıya aittir**.
2. **Geliştirici Sorumluluğunun Reddi:** Yazılım geliştiricisi ve katkıda bulunanlar; bu yazılımın kullanımından, yanlış kullanımından veya kullanılamamasından doğabilecek hiçbir doğrudan, dolaylı, hukuki, idari, cezai veya teknik sonuçtan **sorumlu tutulamaz**.
3. **Garanti Yoktur ("Olduğu Gibi" / "AS-IS"):** Bu yazılım herhangi bir açık veya zımni garanti olmaksızın "olduğu gibi" sunulmaktadır. Geliştirici, yazılımın belirli bir amaca uygunluğu veya kesintisiz çalışacağı konusunda hiçbir taahhütte bulunmaz.
4. **Üçüncü Taraf Hizmetler:** DCBypass herhangi bir üçüncü taraf hizmeti (örneğin Discord veya Cloudflare) temsil etmez, bu kuruluşlarla bağlantılı değildir ve ticari bir amaç gütmez.
