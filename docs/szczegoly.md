# Zapqio Runner — szczegóły

Uzupełnienie głównego [README](../README.md): wymagania, instalacja i odinstalowanie są tam,
tutaj tematy eksploatacyjne — konfiguracja, moduły, zarządzanie usługą, logi, aktualizacja
i rozwiązywanie problemów.

### 1. Konfiguracja

Konfiguracja w pliku `appsettings.json` obok binarki lub przez zmienne środowiskowe. Zmienne
`ZAPQIO_*` mają pierwszeństwo przed wartościami z pliku.

| Klucz (`appsettings.json`) | Zmienna środowiskowa | Opis |
| --- | --- | --- |
| `Token` | `ZAPQIO_TOKEN` | Sekretny token runnera, wydany przez Twoją instancję Web. Web przechowuje wyłącznie skrót Argon2, więc jawną wartość widać tylko w chwili wygenerowania — zgubionego tokenu nie da się odczytać, trzeba wydać nowy. |
| `Name` | `ZAPQIO_NAME` | Stabilna nazwa runnera. Puste = runner generuje UUID i zapisuje go trwale w pliku `##Name` obok binarki. **Nazwa zostaje związana z tokenem przy pierwszym połączeniu i musi już pozostać niezmienna** — inna nazwa przy kolejnym połączeniu to odmowa `401`. |
| `Url` | `ZAPQIO_URL` | Adres instancji Web **bez** sufiksu `/ws-runner` — runner dokleja go sam. |
| `Logger:LogLevel` | `Logger__LogLevel` | Poziom logowania: `Verbose`, `Debug`, `Information`, `Warning`, `Error`, `Fatal` (domyślnie `Information`). |
| `Logger:PathDirectory` | `Logger__PathDirectory` | Katalog logów (domyślnie `Logs` obok binarki); pusty = brak zapisu do pliku. |

**Adres instancji.** Nazwa instancji jest częścią adresu, a nie hosta — instancje stoją pod wspólnym
hostem i rozróżnia je segment ścieżki wybrany przy zakładaniu instancji:

```
wss://app.zapq.io/{instancja}      ← produkcja
ws://localhost:5208/hdwr-test      ← lokalnie
```

Pomyłka w tym segmencie nie daje odmowy protokołu, tylko `404` albo stronę HTML — patrz
[Rozwiązywanie problemów](#6-rozwiązywanie-problemów).

Przykładowy `appsettings.json`:

```json
{
  "Logger": {
    "LogLevel": "Information",
    "PathDirectory": "Logs"
  },
  "Token": "<TOKEN-Z-PANELU-WEB>",
  "Name": "moj-runner-01",
  "Url": "wss://app.zapq.io/moja-instancja"
}
```

**Test w konsoli przed założeniem usługi.** Zanim runner trafi pod menedżera usług, uruchom go
ręcznie **z katalogu instalacji**:

```powershell
cd C:\zapqio\runner
.\Zapqio.Runner.exe
```

Katalog roboczy ma tu znaczenie: uruchomiony z innego miejsca runner szuka `appsettings.json`
w katalogu roboczym, a nie obok binarki, i tam też zapisze `##Name`. Jako usługa działa poprawnie —
katalogiem roboczym jest wtedy `System32`, a host sam wraca do katalogu binarki. Do szybkiego testu
konfigurację można też podać z linii poleceń:

```powershell
.\Zapqio.Runner.exe --Url=wss://app.zapq.io/moja-instancja --Token=<token>
```

### 2. Moduły — czym runner wykonuje zadania

Runner sam z siebie nie ma żadnych metod. Dostarczają je **moduły**: paczki `.zip` wrzucane do
katalogu `Modules` obok binarki.

Przy starcie runner rozpakowuje każdy `.zip` do `.modulesCache\<nazwa-zip>\`, zapisuje sumę SHA-256
paczki w pliku `##Hash` i przy kolejnych startach rozpakowuje ją ponownie tylko wtedy, gdy zip się
zmienił. Moduły ładują się **wyłącznie przy starcie** — po dorzuceniu albo podmianie paczki
zrestartuj usługę.

Moduł to zwykłe biblioteki .NET. Runner skanuje DLL-e z rozpakowanego katalogu i rejestruje publiczne,
nieabstrakcyjne klasy implementujące jeden z interfejsów z projektu
[`Zapqio.Runner.Module.Core`](../Zapqio.Runner.Module.Core/):

- **`IRunnerMethod`** — metoda widoczna w panelu Web:
  - `NameMethod()` — nazwa, po której Web kieruje do niej zadania,
  - `InData()` / `OutData()` — typy wejścia i wyjścia, z których generowany jest JSON Schema
    ogłaszany serwerowi; mogą zwrócić `null`,
  - `Run(string data)` — wykonanie; wejście i wyjście to JSON w postaci ciągu znaków.
- **`IRunnerInjection`** — sam znacznik: klasa trafia do kontenera DI jako singleton i można ją
  wstrzykiwać w pozostałych modułach.

Po pierwszym udanym skanie runner zapisuje w katalogu modułu plik `##Dll` z listą bibliotek,
w których coś znalazł — przy kolejnych startach ładuje już tylko je, zamiast przeglądać wszystkie
DLL-e.

Co warto wiedzieć, pisząc moduł:

- `Console.WriteLine` i `Console.Error.WriteLine` z wnętrza `Run` trafiają na żywo do logów zadania
  w panelu Web (strumień błędów jako poziom `Error`).
- Wyjątek z `Run` kończy zadanie statusem `ERROR`, a jego treść ląduje w logach zadania.
- Wynik idzie jedną wiadomością WebSocket, a serwer przyjmuje najwyżej 32 MiB na wiadomość — rozmiar
  wyniku trzeba ograniczyć po stronie modułu, z zapasem na kodowanie.

### 3. Zarządzanie usługą

```powershell
sc.exe query ZapqioRunner      # status
sc.exe stop ZapqioRunner       # zatrzymanie
sc.exe start ZapqioRunner      # uruchomienie
Restart-Service ZapqioRunner   # restart - po zmianie konfiguracji albo modułów
```

### 4. Logi i diagnostyka

- **Logi plikowe** — katalog wskazany w `Logger:PathDirectory` (domyślnie `Logs` obok binarki), pliki
  rolowane dziennie i po przekroczeniu 200 MB.
- **Konsola** — tylko przy ręcznym uruchomieniu; usługa konsoli nie ma.
- **Windows Event Log** — runner do niego **nie pisze**. W trybie usługi logi plikowe są jedynym
  lokalnym śladem, więc nie zostawiaj `Logger:PathDirectory` pustego. W Podglądzie zdarzeń
  (dziennik `System`, źródło *Service Control Manager*) znajdziesz wyłącznie zdarzenia startu
  i zatrzymania samej usługi.
- **Logi zadań** — to, co metoda wypisze na konsolę, idzie do panelu Web, a równolegle do logu
  plikowego pod źródłem `Method(<nazwa-metody>)-<id-zadania>`.

Czy runner poprawnie połączył się z Web — szukaj w logu:

| Wpis w logu | Znaczenie |
| --- | --- |
| `Successfully connected to WebSocket at wss://…/ws-runner` | Uzgodnienie się powiodło (HTTP 101). |
| `Add method: <typ> in module: <moduł>` | Metoda z modułu została zarejestrowana i pójdzie do Web w wiadomości `Info`. |
| `Kolejna próba połączenia za <n>s (nieudanych z rzędu: <k>)` | Web nieosiągalny albo uzgodnienie odrzucone; runner ponawia sam. |
| `Serwer ogranicza tempo uzgodnień (429)` | Limit po stronie Web — patrz niżej. |
| `Serwer odrzucił uzgadnianie ze statusem <kod>` | Odmowa protokołu; kod rozstrzyga przyczynę. |

Po stronie Web potwierdzeniem jest pojawienie się runnera wraz z listą jego metod w panelu.

### 5. Aktualizacja

Ponowne uruchomienie [`install.ps1`](../install.ps1) (bez parametrów = najnowsza wersja)
zatrzymuje usługę, podmienia pliki i uruchamia ją z powrotem — konfiguracja zostaje.
Ręcznie:

```powershell
sc.exe stop ZapqioRunner
# podmiana plików z nowej paczki - patrz lista poniżej
sc.exe start ZapqioRunner
```

Z katalogu instalacji **zachowaj** (skrypt robi to sam):

- `appsettings.json` — konfiguracja,
- `##Name` — tożsamość runnera. Skasowany, wygeneruje się od nowa jako inny UUID, a Web odrzuci
  połączenie kodem `401`, bo nazwa jest trwale związana z tokenem,
- `Modules\` — paczki modułów,
- `Logs\` — jeśli zależy Ci na historii.

`.modulesCache\` można skasować — odtworzy się przy starcie z paczek w `Modules\`.

Wersję zainstalowanej binarki sprawdzisz przez:

```powershell
(Get-Item C:\zapqio\runner\Zapqio.Runner.exe).VersionInfo.ProductVersion
```

### 6. Rozwiązywanie problemów

| Objaw | Możliwa przyczyna / rozwiązanie |
| --- | --- |
| Usługa nie startuje, w Dzienniku zdarzeń `You must install .NET to run this application` albo `The framework 'Microsoft.WindowsDesktop.App', version '8.0.0' was not found` | Paczka jest framework-dependent i wymaga **.NET Desktop Runtime (x64)** w wersji zgodnej z paczką: domyślnej `win-x64-net8` odpowiada runtime 8, paczce `win-x64` (instalacja z `-Net10`) — runtime 10. Sam .NET Runtime bez *Desktop* nie wystarczy. `winget install Microsoft.DotNet.DesktopRuntime.8` (albo `.10`), instalatory na [dotnet.microsoft.com](https://dotnet.microsoft.com/download/dotnet); sprawdzisz przez `dotnet --list-runtimes`. |
| Usługa nie startuje | Najczęściej zła ścieżka w `binPath=` albo brak prawa zapisu w katalogu instalacji — przy pierwszym starcie runner zapisuje tam `##Name` i bez tego nie ruszy. Uruchom binarkę ręcznie z katalogu instalacji, żeby zobaczyć błąd na konsoli. |
| W logu `401` | Zły token, zła nazwa albo zły segment instancji w `Url`. Nazwa musi być identyczna z tą powiązaną przy pierwszym połączeniu — sprawdź, czy `##Name` nie zniknął przy aktualizacji. Token istnieje tylko w bazie swojej instancji, więc poprawny token pod adresem innej instancji też kończy się `401`. |
| `404` albo strona HTML zamiast `101` | Żądanie nie trafiło w punkt końcowy tej instancji. Sprawdź segment instancji w `Url` i to, że `Url` **nie** kończy się na `/ws-runner`. |
| W logu `426 Upgrade Required` | Web mówi inną główną wersją protokołu niż runner. Zaktualizuj runnera do wersji zgodnej z instancją (odpowiedź niesie wersję serwera w nagłówku `X-Zapqio-Protocol-Version`). |
| `429` / `Serwer ogranicza tempo uzgodnień` | Limit uzgodnień po stronie Web; odmowa zapada przed sprawdzeniem tokenu, więc nie mówi o nim nic. Runner odczekuje sam: honoruje `Retry-After` (do 300 s), a bez tego nagłówka wycofuje się narastająco od 3 s do 60 s z losowym rozrzutem. Jeśli wraca uporczywie, sprawdź, czy spod tego samego adresu nie łączy się naraz wiele runnerów. |
| Runner widoczny w panelu, ale bez metod | Przy starcie runner loguje `Katalog modułów: <ścieżka> (konto: <użytkownik>)`, listę znalezionych paczek, każdy `Add method:` i na koniec `Wysyłam Info: N metod: ...` (albo ostrzeżenie, że metod nie ma). Jeśli paczek nie widać albo jest `Brak dostępu do katalogu modułów`, to konto usługi `NT SERVICE\ZapqioRunner` nie ma prawa odczytu — zdarza się, gdy katalog `Modules\` został **przeniesiony** (nie skopiowany) z profilu użytkownika, bo zachowuje wtedy stare uprawnienia. Sprawdź `icacls C:\zapqio\runner\Modules /T`, napraw przez `icacls C:\zapqio\runner\Modules /reset /T` (przywraca dziedziczenie z katalogu instalacji) albo uruchom ponownie `install.ps1`. Jeśli paczki są, ale DLL się nie ładują, w logu jest `Nie udało się załadować <dll>` z powodem — np. blokada polityki kontroli aplikacji (WDAC / Smart App Control) albo moduł zbudowany pod inną wersję .NET. Poziom `Debug` dokłada `Unppack module:` i `Check dll:`. |
| W logu zadania `Not found method: <nazwa>` | Web kieruje zadanie po nazwie z `NameMethod()`. Moduł nie został załadowany (restart po dorzuceniu paczki) albo nazwa metody rozjechała się z tą użytą w pipeline. |
| `Failed to send JobReturn ... the result of this job was lost` | Połączenie padło między odebraniem zadania a odesłaniem wyniku; Web zamknie takie zadanie jako osierocone. Przy dużych wynikach sprawdź limit 32 MiB na wiadomość — jego przekroczenie zamyka połączenie kodem WS `1009`. |
| Konfiguracja ignorowana przy ręcznym uruchomieniu | `appsettings.json` jest czytany z katalogu roboczego — uruchamiaj binarkę po `cd C:\zapqio\runner`. Trybu usługi to nie dotyczy. |
