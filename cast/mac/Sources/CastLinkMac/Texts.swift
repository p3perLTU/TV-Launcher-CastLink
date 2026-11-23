// The window's texts in Lithuanian and English (the Mac's language decides, as on Windows).

import Foundation

struct Texts {
    let lt: Bool

    static let current = Texts(lt: (Locale.preferredLanguages.first ?? "en").lowercased().hasPrefix("lt"))

    var subtitle: String { lt ? "Šio Mac ekranas iPad'e, telefone ar kitoje naršyklėje" : "This Mac's screen on an iPad, a phone or in another browser" }
    var browser: String { lt ? "Naršyklė" : "Browser" }
    var screens: String { lt ? "Ekranai" : "Screens" }
    var webHint: String { lt ? "iPad, telefonas ar kitas kompiuteris – be programos: jo naršyklėje atidarykite šio Mac adresą." : "An iPad, a phone or another computer – no app: open this Mac's address in its browser." }
    var sound: String { lt ? "Siųsti garsą" : "Send sound" }
    var control: String { lt ? "Leisti valdyti lietimu, pele ir klaviatūra" : "Allow control by touch, mouse and keyboard" }
    var show: String { lt ? "Rodyti naršyklėje" : "Show in a browser" }
    var stop: String { lt ? "Sustabdyti" : "Stop" }
    var starting: String { lt ? "Paleidžiama…" : "Starting…" }
    var openInBrowser: String { lt ? "Įrenginio naršyklėje atidarykite" : "In the device's browser open" }
    var code: String { lt ? "Kodas" : "Code" }
    var screensHint: String { lt ? "Virtualus ekranas paverčia iPad'ą ar telefoną atskiru, išplėstu Mac ekranu. Tikras ekranas rodomas kaip kopija." : "A virtual screen makes the iPad or phone a separate, extended screen of this Mac. A real screen is shown as a copy." }
    var screenToShow: String { lt ? "Rodomas ekranas" : "Screen to show" }
    var virtualScreen: String { lt ? "Virtualus antras ekranas" : "Virtual second screen" }
    var main: String { lt ? "pagrindinis" : "main" }
    var device: String { lt ? "Įrenginys (virtualaus ekrano dydis)" : "Device (the virtual screen's size)" }
    var custom: String { lt ? "Kitas dydis" : "Custom size" }
    var width: String { lt ? "Plotis" : "Width" }
    var height: String { lt ? "Aukštis" : "Height" }
    var retina: String { lt ? "Retina: ryškus, stambus tekstas (daugiau vietos – Ekrano nustatymuose)" : "Retina: sharp, large text (more room in Display settings)" }
    // sharing with a Windows PC (the same words as CastLink.exe)
    var shareStart: String { lt ? "Bendrinti ekraną" : "Share screen" }
    var shareStop: String { lt ? "Nebebendrinti" : "Stop sharing" }
    var shareOff: String { lt ? "Ekranas nebebendrinamas" : "Screen no longer shared" }
    var shareHint: String { lt ? "Windows kompiuteris su CastLink (puslapis „Šis kompiuteris“) gali pasirinkti šį Mac ir jį rodyti bei valdyti." : "A Windows PC with CastLink (the “This PC” page) can pick this Mac, show it and control it." }
    var sharePortBusy: String { lt ? "Prievadas 47802 užimtas: gal CastLink jau atidarytas kitame lange?" : "Port 47802 is busy: is CastLink already open in another window?" }
    func shareWaiting(_ name: String) -> String { lt ? "Ekranas bendrinamas · laukiama kompiuterio. Jame CastLink pasirink „\(name)“ ir spausk „Rodyti“." : "Screen shared · waiting for the PC. In CastLink there pick “\(name)” and press “Show”." }
    var displaySettings: String { lt ? "Ekrano nustatymai" : "Display settings" }
    var appliesNext: String { lt ? "Pakeitimai galioja kitą kartą paspaudus „Rodyti“." : "Changes apply the next time you press “Show”." }
    var virtualHow: String {
        lt ? "Ekrano nustatymuose CastLink ekraną padėkite šalia pagrindinio ir nutempkite ant jo langus. Ten pat galite pasirinkti didesnį dydį: daugiau vietos, smulkesnis tekstas."
           : "In Display settings place the CastLink screen next to the main one and drag windows onto it. There you can also pick a larger size: more room, smaller text."
    }
    var stopped: String { lt ? "Sustabdyta." : "Stopped." }
    var ready: String { lt ? "Paspauskite „Rodyti naršyklėje“." : "Press “Show in a browser”." }
    var waiting: String { lt ? "Laukiama naršyklės…" : "Waiting for a browser…" }
    func connected(_ a: String) -> String { lt ? "Prisijungė: \(a)" : "Connected: \(a)" }
    func left(_ a: String) -> String { lt ? "Žiūrovas atsijungė (\(a)). Laukiama naršyklės…" : "The viewer disconnected (\(a)). Waiting for a browser…" }
    var slow: String { lt ? "Žiūrovas atsijungė: Wi-Fi nespėjo priimti vaizdo" : "The viewer disconnected: the Wi-Fi could not keep up" }
    func wrongCode(_ a: String) -> String { lt ? "Neteisingas kodas iš \(a)" : "Wrong code from \(a)" }
    func locked(_ a: String) -> String { lt ? "5 neteisingi kodai (paskutinis iš \(a)): 5 min. užrakinta, kodas pakeistas" : "5 wrong codes (last from \(a)): locked for 5 min, new code" }
    var noNetwork: String { lt ? "Nėra tinklo: prijunkite Mac prie to paties Wi-Fi kaip įrenginį." : "No network: connect this Mac to the same Wi-Fi as the device." }
    func showing(_ w: Int, _ h: Int, _ fps: Int, _ mbps: Int, _ gpu: Bool) -> String {
        "\(w)×\(h) · \(fps) " + (lt ? "kadr./s" : "frames/s") + " · \(mbps) Mb/s · " + (gpu ? (lt ? "koduoja vaizdo plokštė" : "GPU encoder") : (lt ? "koduoja procesorius" : "processor encoder"))
    }
    var needScreen: String { lt ? "Reikia leidimo įrašyti ekraną: Nustatymuose įjunkite CastLink, tada programą uždarykite ir atidarykite iš naujo." : "Screen recording is not allowed: turn on CastLink in Settings, then quit and reopen the app." }
    var needAccessibility: String { lt ? "Valdymui reikia „Accessibility“ leidimo: Nustatymuose įjunkite CastLink." : "Control needs the Accessibility permission: turn on CastLink in Settings." }
    var openSettings: String { lt ? "Atidaryti nustatymus" : "Open Settings" }
    var portBusy: String { lt ? "Prievadas 47900 užimtas (gal CastLink jau atidaryta?)" : "Port 47900 is busy (is CastLink already open?)" }
    var virtualFailed: String { lt ? "macOS nesukūrė virtualaus ekrano. Pasirinkite tikrą ekraną." : "macOS did not make a virtual screen. Pick a real screen." }
    var captureStopped: String { lt ? "Ekrano įrašymas nutrūko." : "Screen capture stopped." }

    // ---- the CastLink app page (TV and Android phone) ----
    var appSection: String { lt ? "CastLink programa" : "CastLink app" }
    var appHint: String { lt ? "Televizorius, Android telefonas ar planšetė su „CastLink“ programa. Telefonas gali valdyti šį Mac lietimu." : "A TV, an Android phone or tablet with the “CastLink” app. A phone can control this Mac by touch." }
    var deviceLabel: String { lt ? "Įrenginys (pavadinimas arba IP adresas)" : "Device (name or IP address)" }
    var find: String { lt ? "Ieškoti" : "Find" }
    var searching: String { lt ? "Ieškoma įrenginių… (juose turi būti atidaryta „CastLink“)" : "Searching… (“CastLink” must be open on the device)" }
    func found(_ n: Int) -> String { lt ? "Rasta įrenginių: \(n). Spauskite „Rodyti“." : "Devices found: \(n). Press “Show”." }
    var notFound: String { lt ? "Nieko nerasta. Įrenginyje atidarykite „CastLink“ arba įrašykite jo IP adresą." : "Nothing found. Open “CastLink” on the device or type its IP address." }
    var enterIp: String { lt ? "Įrašykite įrenginio IP adresą (jį rodo „CastLink“)." : "Type the device's IP address (“CastLink” on it shows it)." }
    var fps120: String { lt ? "120 kadr./s (120 Hz ekranams)" : "120 frames/s (120 Hz screens)" }
    var showOnDevice: String { lt ? "Rodyti" : "Show" }
    func connecting(_ a: String) -> String { lt ? "Jungiamasi prie \(a)…" : "Connecting to \(a)…" }
    func pairWaiting(_ n: String) -> String { lt ? "Palyginkite kodą „\(n)“ ekrane" : "Compare the code on the screen of “\(n)”" }
    var pairTitle: String { lt ? "Pirmas kartas su šiuo įrenginiu" : "First time with this device" }
    func pairAsk(_ n: String, _ code: String) -> String {
        lt ? "Ar „\(n)“ ekrane rodomas šis kodas?\n\n\(code)\n\nTaip – kodas sutampa (įrenginys bus įsimintas).\nNe – nesutampa: gal kažkas kitas bando prisijungti."
           : "Does the screen of “\(n)” show this code?\n\n\(code)\n\nYes: the code matches (the device will be remembered).\nNo: it does not, someone else may be trying to connect."
    }
    var yes: String { lt ? "Taip" : "Yes" }
    var no: String { lt ? "Ne" : "No" }
    var pairRefused: String { lt ? "Kodas nesutapo: rodymas sustabdytas" : "The code did not match: casting stopped" }
    var oldReceiver: String { lt ? "Imtuvas neatsako. Jei jame sena CastLink versija, atnaujinkite ją." : "The receiver does not answer. If it has an older CastLink, update it." }
    func showingOn(_ n: String) -> String { lt ? "Rodoma: \(n)" : "Showing on \(n)" }
    func liveStats(_ fps: Int, _ mbps: Double, _ w: Int, _ h: Int, _ gpu: Bool) -> String {
        "\(w)×\(h) · \(fps) " + (lt ? "kadr./s" : "frames/s") + " · " + String(format: "%.1f", mbps) + " Mb/s · " + (gpu ? (lt ? "koduoja vaizdo plokštė" : "GPU encoder") : (lt ? "koduoja procesorius" : "processor encoder"))
    }
}
