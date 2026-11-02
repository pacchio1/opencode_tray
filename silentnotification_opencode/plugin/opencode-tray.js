/**
 * opencode-tray - plugin bridge
 *
 * Invia lo stato delle sessioni opencode all'icona nella notification area di Windows:
 *
 *   idle      -> icona nera    (ho finito / sto aspettando che tu scriva)
 *   working   -> icona verde   (sta lavorando: modello o tool in corso)
 *   attention -> icona gialla  (serve davvero una tua risposta: domanda,
 *                               permissione, errore, connessione instabile)
 *
 * Il giallo resta acceso finche' la richiesta non e' stata soddisfatta, quindi
 * NON si accende a ogni fine turno: se opencode ha semplicemente finito di
 * scrivere e aspetta il prossimo messaggio, l'icona resta nera.
 *
 * Ogni sessione viene seguita separatamente: il tray raggruppa per progetto
 * (cartella di lavoro), cosi' si vede quale progetto sta andando e quale e'
 * bloccato in attesa di una risposta.
 *
 * La stima dei costi e' "quanto pagherei se il modello non fosse gratis":
 * i modelli gratuiti (es. "-free") vengono valutati con il listino del
 * gemello a pagamento (es. minimax-m3.1-flash-free -> minimax-m3).
 * Nella stima entrano SOLO i token dei provider di opencode ("opencode",
 * "opencode-go"): quelli passati da openrouter hanno un listino diverso e
 * vengono esclusi.
 *
 * L'app tray ascolta su 127.0.0.1:47110 (variabile OPENCODE_TRAY_PORT per cambiarla).
 * Se l'app tray non e' in esecuzione la richiesta fallisce e viene ignorata.
 */

const PORT = Number(process.env.OPENCODE_TRAY_PORT || 47110)
const ENDPOINT = "http://127.0.0.1:" + PORT + "/state"
const HEARTBEAT_MS = 4000
const TIMEOUT_MS = 800

const INSTANCE = String(process.pid || "0")

// versione del plugin: il tray non la usa, serve solo a verificare quale
// versione e' stata caricata in memoria da ciascun processo opencode
const PLUGIN_V = 12

const REASON_QUESTION = "opencode ti chiede una risposta"
const REASON_PERMISSION = "opencode chiede una permissione"
const REASON_ERROR = "errore durante l'esecuzione"
const REASON_RETRY = "connessione instabile: sto riprovando"

const QUESTION_TOOLS = ["question", "ask_user", "askuser", "ask", "ask_user_question"]

// nomi dei tool gia' visti (diagnostica: quale tool chiede all'utente)
const seenTools = new Set()

// sessionID -> entry
//   state    : idle | working | attention (stato mostrato)
//   reason   : testo dello stato mostrato
//   pending  : richiesta aperta che aspetta l'utente; se valorizzata forza
//              "attention" anche a fine turno, finche' non viene soddisfatta
//   cwd      : cartella di lavoro della sessione (dal messaggio)
//   project  : nome della cartella di lavoro
//   activity : cosa sta facendo adesso (tool in corso, "risposta", "modello")
//   lastModel: ultimo modello/provider visto in questa sessione
const sessions = new Map()

let sent = { state: "idle", reason: "" }
let inFlight = false
let pending = null
let lastEventAt = Date.now()

// messageID -> { providerID, modelID, sessionID, cwd, project }
// Il modello sta nel messaggio assistant.
const msgModel = new Map()

// Totali cumulativi per sessione e modello: sessionID -> Map("provider\0model" -> stats)
// Sono la fonte di verita' del totale: non vengono mai potati, cosi' il costo
// non "torna indietro" quando la finestra temporale scade.
const acc = new Map()

// Costo per ora ("yyyy-MM-dd HH"): serve per le finestre 24 ore / 7 giorni e
// per la proiezione settimanale. Viene potato a 31 giorni.
const buckets = new Map()

// Step gia' contati (partID -> { sid, at }): evita di contare due volte lo
// stesso step e viene potato a 14 giorni.
const seenSteps = new Map()

// Metadati delle sessioni (sid -> { project, cwd, firstAt }): salvati su disco
// cosi' il totale non riparte da zero quando opencode viene riavviato.
const sessionMeta = new Map()

const BUCKET_KEEP_MS = 31 * 24 * 3600 * 1000
const SEEN_KEEP_MS = 14 * 24 * 3600 * 1000
const SESSION_KEEP_MS = 45 * 24 * 3600 * 1000
const SAVE_EVERY_MS = 20000

// diagnostica: tipi di evento gia' visti (per capire cosa arriva dalla bus)
const seenEvents = new Set()
let lastDiagTotal = -1

// Listino prezzi ($ per milione di token) preso dal registry locale di opencode
// (~/.cache/opencode/models.json): serve a stimare il costo anche per i modelli
// "gratis" (o serviti via abbonamento) per cui opencode riporta cost = 0.
let priceTable = null
let priceTableStamp = -1

async function loadPrices(force) {
  if (priceTable !== null && !force) return priceTable
  const table = {}
  let stamp = -1
  try {
    const fs = await import("node:fs")
    const os = await import("node:os")
    const path = await import("node:path")
    const file = path.join(os.homedir(), ".cache", "opencode", "models.json")
    try {
      stamp = Math.floor(fs.statSync(file).mtimeMs)
    } catch {}
    const reg = JSON.parse(fs.readFileSync(file, "utf8"))
    for (const pid of Object.keys(reg)) {
      const models = reg[pid] && reg[pid].models
      if (!models) continue
      for (const mid of Object.keys(models)) {
        if (models[mid] && models[mid].cost) table[pid + "\u0000" + mid] = models[mid].cost
      }
    }
  } catch {}
  priceTable = table
  priceTableStamp = stamp
  return priceTable
}

// opencode aggiorna models.json nel tempo: se il file e' cambiato si rilegge
// il listino, altrimenti la stima resta ferma a prezzi vecchi.
async function reloadPricesIfChanged() {
  try {
    const fs = await import("node:fs")
    const os = await import("node:os")
    const path = await import("node:path")
    const file = path.join(os.homedir(), ".cache", "opencode", "models.json")
    let stamp = -1
    try {
      stamp = Math.floor(fs.statSync(file).mtimeMs)
    } catch {}
    if (stamp < 0 || stamp === priceTableStamp) return
    await loadPrices(true)
    await diag("listino ricaricato: " + Object.keys(priceTable || {}).length + " modelli")
  } catch {}
}

// Modelli senza listino proprio (gratis): la stima usa il prezzo di un altro
// modello come riferimento. big-pickle (gratis) -> prezzi di minimax-m3.
const PRICE_ALIASES = {
  "big-pickle": { providerID: "opencode", modelID: "minimax-m3" },
  "big-pickle-free": { providerID: "opencode", modelID: "minimax-m3" },
  "muse-spark-1.3-contributor-free": { providerID: "opencode", modelID: "muse-spark-1.3" },
  "space-bunny-free": { providerID: "opencode-go", modelID: "space-bunny" },
}

// Solo i provider di opencode entrano nella stima: openrouter (e gli altri
// aggregatori) hanno un listino proprio e non vanno mescolati.
function isOpencodeProvider(providerID) {
  if (!providerID) return false
  const p = String(providerID).toLowerCase()
  return p === "opencode" || p.indexOf("opencode-") === 0
}

function hasRate(c) {
  if (!c) return false
  return (
    (c.input || 0) > 0 ||
    (c.output || 0) > 0 ||
    (c.cache_read || 0) > 0 ||
    (c.cache_write || 0) > 0
  )
}

// "gemello a pagamento" di un modello gratis: stesso provider, nome piu'
// vicino possibile (es. minimax-m3.1-flash-free -> minimax-m3).
// Vince il modello con il prefisso comune piu' lungo col nome del modello gratis.
function paidTwin(providerID, modelID) {
  const t = priceTable || {}
  const base = String(modelID).replace(/-(contributor-)?free$/i, "")
  if (!base) return null
  const prefix = providerID + "\u0000"
  let best = null
  for (const k of Object.keys(t)) {
    if (k.indexOf(prefix) !== 0) continue
    const cost = t[k]
    if (!hasRate(cost)) continue
    const mid = k.slice(providerID.length + 1)
    if (mid === modelID) continue
    let common = 0
    const max = Math.min(mid.length, base.length)
    while (common < max && mid[common] === base[common]) common++
    // il nome deve essere un "parente", non un modello completamente diverso
    if (common < 4) continue
    if (mid.indexOf(base) !== 0 && base.indexOf(mid) !== 0) continue
    if (!best || common > best.common || (common === best.common && mid.length < best.modelID.length))
      best = { price: cost, modelID: mid, common: common }
  }
  return best ? { price: best.price, modelID: best.modelID } : null
}

// Prezzo da usare per (provider, modello): { price, as } dove "as" e' il
// modello con cui e' stato valutato (per l'etichetta).
function rateFor(providerID, modelID) {
  const t = priceTable || {}
  if (!providerID || !modelID) return null
  const direct = t[providerID + "\u0000" + modelID]
  if (direct && hasRate(direct)) return { price: direct, as: modelID }
  const alias = PRICE_ALIASES[modelID]
  if (alias) {
    const ac = t[alias.providerID + "\u0000" + alias.modelID]
    if (ac && hasRate(ac)) return { price: ac, as: alias.modelID }
  }
  const twin = paidTwin(providerID, modelID)
  if (twin) return { price: twin.price, as: twin.modelID }
  // fallback: stesso modelID sotto un altro provider
  const suffix = "\u0000" + modelID
  for (const k of Object.keys(t)) if (k.endsWith(suffix)) return { price: t[k], as: modelID }
  return null
}

// Alcuni modelli hanno prezzi diversi quando il contesto supera la soglia
// (es. "context_over_200k" o la lista "tiers"): qui si sceglie la fascia giusta.
function ratesFor(price, inputTokens) {
  let p = price
  if (!p) return p
  if (p.context_over_200k && inputTokens > 200000) return p.context_over_200k
  if (Array.isArray(p.tiers) && p.tiers.length > 0) {
    let best = null
    for (const t of p.tiers) {
      if (!t || !hasRate(t)) continue
      const size = tierSize(t.tier)
      if (!size || inputTokens <= size) continue
      if (!best || size > best.size) best = { size: size, rates: t }
    }
    if (best) return best.rates
  }
  return p
}

function tierSize(tier) {
  if (!tier || typeof tier !== "string") return 0
  const m = /size=(\d+)/.exec(tier)
  return m ? parseInt(m[1], 10) : 0
}

// Stima il costo di uno step dai token per milione, secondo il listino.
// I token di "reasoning" si pagano come l'output; il "cache_write", dove il
// listino non lo espone, si assume al prezzo dell'input.
// Se il modello non e' nel listino si usa il costo dichiarato dal provider
// (part.cost), che e' gia' il costo dello step (NON quello del messaggio:
// quello moltiplicato per gli step gonfierebbe la stima).
function estimateCost(u) {
  const r = rateFor(u.providerID, u.modelID)
  if (!r) return u.providerCost || 0
  const price = ratesFor(r.price, u.input || 0)
  const inRate = price.input || 0
  const outRate = price.output || 0
  const crRate = price.cache_read != null ? price.cache_read : inRate
  const cwRate = price.cache_write != null ? price.cache_write : inRate
  const output = (u.output || 0) + (u.reasoning || 0)
  const cost =
    (u.input || 0) * inRate +
    output * outRate +
    (u.cacheRead || 0) * crRate +
    (u.cacheWrite || 0) * cwRate
  return cost / 1e6
}

// Restituisce l'ID del modello il cui prezzo viene applicato (uguale al modello,
// o l'alias se il modello e' gratis senza listino). Serve da etichetta.
function pricedAsFor(providerID, modelID) {
  const r = rateFor(providerID, modelID)
  return r ? r.as : modelID
}

function round6(v) {
  return Math.round((v || 0) * 1000000) / 1000000
}

// Modello (provider + id) da usare per uno step: quello del messaggio
// assistant; se non e' ancora noto si usa l'ultimo visto nella sessione.
function modelOf(messageID, sid) {
  const m = msgModel.get(messageID)
  if (m && m.modelID) return { providerID: m.providerID || "", modelID: m.modelID }
  const e = sessions.get(sid)
  if (e && e.lastModel && e.lastModel.modelID) return e.lastModel
  return null
}

// Aggiunge uno step ai totali cumulativi della sessione.
function addStep(sid, model, tk, cache, providerCost) {
  const key = model.providerID + "\u0000" + model.modelID
  let per = acc.get(sid)
  if (!per) {
    per = new Map()
    acc.set(sid, per)
  }
  let a = per.get(key)
  if (!a) {
    a = {
      providerID: model.providerID,
      modelID: model.modelID,
      pricedAs: pricedAsFor(model.providerID, model.modelID),
      input: 0,
      output: 0,
      reasoning: 0,
      cacheRead: 0,
      cacheWrite: 0,
      cost: 0,
    }
    per.set(key, a)
  }
  const cost = estimateCost({
    providerID: model.providerID,
    modelID: model.modelID,
    input: tk.input || 0,
    output: tk.output || 0,
    reasoning: tk.reasoning || 0,
    cacheRead: cache.read || 0,
    cacheWrite: cache.write || 0,
    providerCost: providerCost || 0,
  })
  a.input += tk.input || 0
  a.output += tk.output || 0
  a.reasoning += tk.reasoning || 0
  a.cacheRead += cache.read || 0
  a.cacheWrite += cache.write || 0
  a.cost += cost

  const meta = metaOf(sid)
  if (!meta.firstAt) meta.firstAt = Date.now()

  // costo orario: base delle finestre 24h / 7gg
  const bucket = bucketKey(Date.now())
  let perBucket = buckets.get(sid)
  if (!perBucket) {
    perBucket = new Map()
    buckets.set(sid, perBucket)
  }
  perBucket.set(bucket, (perBucket.get(bucket) || 0) + cost)
  return cost
}

function metaOf(sid) {
  let m = sessionMeta.get(sid)
  if (!m) {
    m = { project: "", cwd: "", firstAt: 0 }
    sessionMeta.set(sid, m)
  }
  return m
}

function bucketKey(ts) {
  const d = new Date(ts)
  const p = (v, n) => String(v).padStart(n || 2, "0")
  return d.getFullYear() + "-" + p(d.getMonth() + 1) + "-" + p(d.getDate()) + " " + p(d.getHours())
}

// Totali cumulativi per sessione (dal vivo, senza finestre temporali).
function aggregate() {
  return acc
}

function totalCost() {
  let total = 0
  for (const per of acc.values()) for (const a of per.values()) total += a.cost
  return round6(total)
}

// Costo degli ultimi N ms (finestra temporale), per sessione: letta dai
// cesti orari, quindi sopravvive al riavvio di opencode.
function windowCost(sid, ms) {
  const limit = Date.now() - ms
  const limitKey = bucketKey(limit)
  const per = buckets.get(sid)
  if (!per) return 0
  let sum = 0
  for (const [k, v] of per) {
    if (k >= limitKey) sum += v
  }
  return round6(sum)
}

// Riga compatta "un modello per record" (il parser C# e' minimale):
// provider|model|pricedAs|input|output|reasoning|cacheRead|cacheWrite|cost
function modelsLine() {
  const parts = []
  for (const per of acc.values()) {
    for (const a of per.values()) {
      parts.push([
        a.providerID, a.modelID, a.pricedAs,
        a.input, a.output, a.reasoning, a.cacheRead, a.cacheWrite,
        round6(a.cost),
      ].join("|"))
    }
  }
  return parts.join(",")
}

// Riga per sessione (un record per sessione, righe separate da "\n"):
//   sid|project|cwd|state|reason|activity|costo24h|costo7d|primoStep|modelli
// I record dei modelli sono separati da "," (ogni record ha 9 campi "|").
function sessionsLine() {
  const lines = []
  // solo le sessioni vive: quelle ripristinate dal file ma non piu' aperte
  // non devono comparire nel tray (il costo resta pero' li' accumulato)
  const ids = new Set()
  for (const sid of sessions.keys()) ids.add(sid)
  for (const sid of ids) {
    const e = sessions.get(sid) || { state: "idle", reason: "", project: "", cwd: "", activity: "" }
    const meta = metaOf(sid)
    const per = acc.get(sid)
    const recs = []
    if (per) {
      for (const a of per.values()) {
        recs.push([
          a.providerID, a.modelID, a.pricedAs,
          a.input, a.output, a.reasoning, a.cacheRead, a.cacheWrite,
          round6(a.cost),
        ].join("|"))
      }
    }
    lines.push([
      clean(sid), clean(e.project || meta.project || ""), clean(e.cwd || meta.cwd || ""),
      e.state || "idle", clean(e.reason || ""), clean(e.activity || ""),
      windowCost(sid, 24 * 3600 * 1000),
      windowCost(sid, 7 * 24 * 3600 * 1000),
      meta.firstAt || 0,
      recs.join(","),
    ].join("|"))
  }
  return lines.join("\n")
}

// I campi del protocollo sono separati da "|": i caratteri di separazione
// vengono sostituiti per non rompere la parsing lato tray.
function clean(s) {
  return String(s == null ? "" : s).replace(/[|;,\r\n]/g, " ").trim()
}

// Toglie gli step piu' vecchi (non servono piu' per il controllo dei duplicati).
function pruneSeenSteps() {
  const limit = Date.now() - SEEN_KEEP_MS
  for (const [id, s] of seenSteps) {
    if (s.at && s.at < limit) seenSteps.delete(id)
  }
}

// ------------------------------------------------------------- stato su disco
// Lo stato delle sessioni (token, costi, cesti orari) viene salvato su file:
// se opencode viene riavviato il totale NON riparte da zero.
const STATE_FILE = "plugin-state.txt"
let stateDirty = false
let lastSaveAt = 0

// cartella di stato/log: su Windows %APPDATA%\opencode-tray, su Linux
// ~/.config/opencode-tray (stessa cartella usata dall'estensione GNOME)
function configDir() {
  const appdata = process.env.APPDATA || ""
  if (appdata) return appdata + "\\opencode-tray"
  const home = process.env.HOME || ""
  if (!home) return null
  return home + "/.config/opencode-tray"
}

function statePath() {
  const dir = configDir()
  if (!dir) return null
  const sep = dir.indexOf("\\") >= 0 ? "\\" : "/"
  return dir + sep + STATE_FILE
}

function safeField(s) {
  return String(s == null ? "" : s).replace(/[|\r\n]/g, " ")
}

// Carica lo stato salvato in precedenza (alla partenza di opencode).
async function loadState() {
  const file = statePath()
  if (!file) return
  try {
    const fs = await import("node:fs")
    let text = ""
    try {
      text = fs.readFileSync(file, "utf8")
    } catch {
      return
    }
    let restored = 0
    for (const line of text.split("\n")) {
      if (!line) continue
      const f = line.split("|")
      if (f[0] === "S" && f.length >= 5) {
        const sid = f[1]
        const m = metaOf(sid)
        m.project = f[2]
        m.cwd = f[3]
        m.firstAt = parseInt(f[4], 10) || 0
        restored++
      } else if (f[0] === "M" && f.length >= 11) {
        const sid = f[1]
        let per = acc.get(sid)
        if (!per) {
          per = new Map()
          acc.set(sid, per)
        }
        const a = {
          providerID: f[2],
          modelID: f[3],
          pricedAs: f[4],
          input: parseInt(f[5], 10) || 0,
          output: parseInt(f[6], 10) || 0,
          reasoning: parseInt(f[7], 10) || 0,
          cacheRead: parseInt(f[8], 10) || 0,
          cacheWrite: parseInt(f[9], 10) || 0,
          cost: parseFloat(f[10]) || 0,
        }
        per.set(a.providerID + "\u0000" + a.modelID, a)
      } else if (f[0] === "B" && f.length >= 4) {
        const sid = f[1]
        let per = buckets.get(sid)
        if (!per) {
          per = new Map()
          buckets.set(sid, per)
        }
        per.set(f[2], parseFloat(f[3]) || 0)
      } else if (f[0] === "T" && f.length >= 4) {
        seenSteps.set(f[2], { sid: f[1], at: parseInt(f[3], 10) || 0 })
      }
    }
    await diag("stato ripristinato: " + restored + " sessioni, " + acc.size + " con token")
  } catch (e) {
    await diag("stato non ripristinato: " + (e && e.message ? e.message : e))
  }
}

// Salva lo stato su file (scrittura atomica: .tmp + rename) e pota le voci
// vecchie: sessioni oltre 45 giorni, cesti oltre 31 giorni, step oltre 14.
async function saveState() {
  const file = statePath()
  if (!file) return
  try {
    const fs = await import("node:fs")
    const path = await import("node:path")
    const now = Date.now()
    const lines = []

    for (const [sid, meta] of sessionMeta) {
      if (meta.firstAt && now - meta.firstAt > SESSION_KEEP_MS) {
        sessionMeta.delete(sid)
        acc.delete(sid)
        buckets.delete(sid)
        continue
      }
      lines.push("S|" + safeField(sid) + "|" + safeField(meta.project) + "|" + safeField(meta.cwd) + "|" + (meta.firstAt || 0))
    }

    for (const [sid, per] of acc) {
      for (const a of per.values()) {
        lines.push([
          "M", safeField(sid), safeField(a.providerID), safeField(a.modelID), safeField(a.pricedAs),
          a.input, a.output, a.reasoning, a.cacheRead, a.cacheWrite, round6(a.cost),
        ].join("|"))
      }
    }

    const limitBucket = bucketKey(now - BUCKET_KEEP_MS)
    for (const [sid, per] of buckets) {
      for (const [k, v] of per) {
        if (k < limitBucket) {
          per.delete(k)
          continue
        }
        lines.push("B|" + safeField(sid) + "|" + k + "|" + round6(v))
      }
      if (per.size === 0) buckets.delete(sid)
    }

    const limitStep = now - SEEN_KEEP_MS
    for (const [id, s] of seenSteps) {
      if (s.at && s.at < limitStep) {
        seenSteps.delete(id)
        continue
      }
      lines.push("T|" + safeField(s.sid) + "|" + safeField(id) + "|" + (s.at || 0))
    }

    fs.mkdirSync(path.dirname(file), { recursive: true })
    const tmp = file + ".tmp"
    fs.writeFileSync(tmp, lines.join("\n") + "\n", "utf8")
    try {
      fs.renameSync(tmp, file)
    } catch {
      fs.copyFileSync(tmp, file)
      try { fs.unlinkSync(tmp) } catch {}
    }
    stateDirty = false
    lastSaveAt = now
  } catch (e) {
    await diag("salvataggio stato fallito: " + (e && e.message ? e.message : e))
  }
}

// diagnostica: scrive una riga in plugin-events.log (solo prime occorrenze/riepiloghi)
let diagCount = 0
async function diag(line) {
  try {
    const fs = await import("node:fs")
    const path = await import("node:path")
    const dir = configDir()
    if (dir) {
      try {
        fs.mkdirSync(dir, { recursive: true })
      } catch {}
      fs.appendFileSync(
        path.join(dir, "plugin-events.log"),
        new Date().toISOString() + "  pid" + INSTANCE + "  " + line + "\n"
      )
    }
  } catch {}
}

// ----------------------------------------------------------------- trasporto
function flush() {
  if (inFlight || !pending) return
  const payload = pending
  pending = null
  inFlight = true
  const ctrl = new AbortController()
  const timer = setTimeout(() => ctrl.abort(), TIMEOUT_MS)
  fetch(ENDPOINT, {
    method: "POST",
    headers: { "content-type": "application/json" },
    body: JSON.stringify({
      state: payload.state,
      reason: payload.reason,
      cost: totalCost(),
      models: modelsLine(),
      sessions: sessionsLine(),
      pid: INSTANCE,
      project: project(),
      cwd: process.cwd(),
      ts: Date.now(),
    }),
    signal: ctrl.signal,
  })
    .catch(() => {})
    .then(() => {
      clearTimeout(timer)
      inFlight = false
      flush()
    })
}

function post(payload) {
  pending = payload
  flush()
}

function project() {
  const dir = process.cwd() || ""
  return dir.length > 0 ? dir.split(/[\\/]/).filter(Boolean).pop() : "opencode"
}

// ----------------------------------------------------------------- stato
function severity(state) {
  if (state === "attention") return 2
  if (state === "working") return 1
  return 0
}

function recompute() {
  let state = "idle"
  let reason = ""
  for (const entry of sessions.values()) {
    if (severity(entry.state) > severity(state)) {
      state = entry.state
      reason = entry.reason || (entry.state === "working" ? entry.activity : "")
    } else if (entry.state === state && state !== "idle" && reason === "") {
      reason = entry.reason || (entry.state === "working" ? entry.activity : "")
    }
  }
  if (state === sent.state && reason === sent.reason) return
  sent = { state, reason }
  post(sent)
}

function entryOf(sid) {
  let entry = sessions.get(sid)
  if (!entry) {
    const cwd = process.cwd() || ""
    const meta = sessionMeta.get(sid)
    entry = {
      state: "idle",
      reason: "",
      pending: "",
      cwd: (meta && meta.cwd) || cwd,
      project: (meta && meta.project) || project(),
      activity: "",
      lastModel: null,
    }
    sessions.set(sid, entry)
  }
  return entry
}

function setState(sid, state, reason) {
  const entry = entryOf(sid)
  const text = reason || ""
  if (entry.state === state && entry.reason === text) return
  // una richiesta aperta verso l'utente ha precedenza: finche' non viene
  // soddisfatta (permission.replied, question completata, nuovo messaggio),
  // nessun segnale di "lavoro" puo' spegnere il giallo
  if (state !== "attention" && entry.pending) return
  entry.state = state
  entry.reason = text
  if (state === "idle") entry.activity = ""
  recompute()
}

// cosa sta facendo la sessione (tool in corso, scrittura della risposta...)
function setActivity(sid, activity) {
  const entry = entryOf(sid)
  if (entry.activity === activity) return
  entry.activity = activity
  if (entry.state === "working") recompute()
}

// richiesta che aspetta l'utente: giallo, e resta giallo anche a fine turno.
// Solo domande e permissione sono "sticky": se l'utente non risponde, il giallo
// deve restare acceso anche quando il turno e' finito.
function setPending(sid, reason) {
  const entry = entryOf(sid)
  entry.pending = reason
  setState(sid, "attention", reason)
}

// attenzione momentanea (errore, retry): si spegne a fine turno come tutto il resto
function setTransient(sid, reason) {
  setState(sid, "attention", reason)
}

// richiesta soddisfatta (l'utente ha risposto): si torna al lavoro
function clearPending(sid) {
  entryOf(sid).pending = ""
}

// fine turno: nero, a meno che ci sia una richiesta ancora aperta.
// All'avvio di opencode arriva un idle "a freddo": nero, come deve essere.
function endOfTurn(sid) {
  const entry = entryOf(sid)
  if (entry.pending) setState(sid, "attention", entry.pending)
  else setState(sid, "idle", "")
}

// rete di sicurezza: nessun evento per STALE_MS mentre non siamo in working e non
// c'e' una richiesta aperta. Serve a non lasciare mai l'icona gialla per sempre se
// un evento si perde (turno interrotto, sessione abbandonata, plugin ricaricato).
const STALE_MS = 45000

function watchdog() {
  if (Date.now() - lastEventAt < STALE_MS) return
  let changed = false
  for (const [sid, entry] of sessions) {
    if (entry.pending) continue
    if (entry.state === "attention" || entry.state === "working") {
      entry.state = "idle"
      entry.reason = ""
      entry.activity = ""
      changed = true
    }
  }
  if (changed) recompute()
}

function isQuestion(tool) {
  return QUESTION_TOOLS.indexOf(tool) >= 0
}

// Il payload degli eventi non ha la stessa forma in tutte le versioni di
// opencode: la v2 usa event.data, la v1 event.properties.
function eventData(event) {
  return (event && (event.data || event.properties)) || {}
}

// La sessione puo' stare in punti diversi del payload: in cima (session.*),
// dentro info (message.updated v1) o dentro form (form.created/form.*).
function sessionIDOf(p) {
  if (!p) return ""
  if (typeof p.sessionID === "string" && p.sessionID.length > 0) return p.sessionID
  if (p.info && typeof p.info.sessionID === "string" && p.info.sessionID.length > 0)
    return p.info.sessionID
  if (p.form && typeof p.form.sessionID === "string" && p.form.sessionID.length > 0)
    return p.form.sessionID
  return ""
}

function sessionOf(properties) {
  return sessionIDOf(properties) || "main"
}

// ----------------------------------------------------------------- plugin
export const OpencodeTrayPlugin = async () => {
  await loadPrices()
  // stato precedente (se opencode era stato chiuso): i token non si perdono
  await loadState()

  try {
    const fs = await import("node:fs")
    const path = await import("node:path")
    const dir = configDir()
    if (dir) {
      try {
        fs.mkdirSync(dir, { recursive: true })
      } catch {}
      fs.appendFileSync(
        path.join(dir, "plugin-versions.log"),
        new Date().toISOString() + "  plugin v" + PLUGIN_V + " caricato (pid " + INSTANCE + ") - listino " + Object.keys(priceTable || {}).length + " modelli\n"
      )
    }
  } catch {}

  const heartbeat = setInterval(() => {
    try {
      watchdog()
    } catch {}
    try {
      reloadPricesIfChanged()
    } catch {}
    try {
      pruneSeenSteps()
    } catch {}
    try {
      const c = totalCost()
      if (c !== lastDiagTotal) {
        lastDiagTotal = c
        diag("HB costo=" + c + " step=" + seenSteps.size + " sessioni=" + sessions.size)
      }
    } catch {}
    // lo stato viene salvato su file ogni 20 secondi: cosi' il totale non
    // riparte da zero quando opencode viene riavviato
    try {
      if (stateDirty || Date.now() - lastSaveAt > SAVE_EVERY_MS * 3) saveState()
    } catch {}
    post(sent)
  }, HEARTBEAT_MS)
  if (heartbeat && typeof heartbeat.unref === "function") heartbeat.unref()

  const handlers = {
    event: async ({ event }) => {
      try {
        lastEventAt = Date.now()
        const p = eventData(event)
        if (!seenEvents.has(event.type)) {
          seenEvents.add(event.type)
          await diag("EVT " + event.type)
        }
        switch (event.type) {
          case "session.created":
            setState(sessionOf(p), "idle", "")
            break

          case "session.deleted":
            sessions.delete(sessionOf(p))
            recompute()
            break

          // opencode v2: il turno inizia con execution.started e finisce con
          // execution.succeeded/interrupted/failed. Questi eventi sono la
          // fonte affidabile per accendere e spegnere l'icona: senza di loro
          // l'icona restava verde dopo la fine della risposta.
          case "session.execution.started":
          case "session.inbox.enqueued":
          case "session.inbox.delivered":
            clearPending(sessionOf(p))
            setState(sessionOf(p), "working", "")
            break

          case "session.execution.succeeded":
          case "session.execution.interrupted":
            endOfTurn(sessionOf(p))
            break

          case "session.execution.failed":
            setTransient(sessionOf(p), REASON_ERROR)
            break

          case "session.retry.scheduled":
            setTransient(sessionOf(p), REASON_RETRY)
            break

          case "session.step.started":
          case "session.step.ended":
            setActivity(sessionOf(p), "modello")
            setState(sessionOf(p), "working", "")
            break

          case "session.step.failed":
            setTransient(sessionOf(p), REASON_ERROR)
            break

          case "session.text.started":
            setActivity(sessionOf(p), "risposta")
            setState(sessionOf(p), "working", "")
            break

          case "session.reasoning.started":
            setActivity(sessionOf(p), "modello")
            setState(sessionOf(p), "working", "")
            break

          case "session.tool.called":
          case "session.tool.success":
            setState(sessionOf(p), "working", "")
            break

          case "session.tool.failed":
            setTransient(sessionOf(p), REASON_ERROR)
            break

          case "session.status": {
            const sid = sessionOf(p)
            const type = p.status && p.status.type
            if (type === "busy") setState(sid, "working", "")
            else if (type === "idle") endOfTurn(sid)
            else if (type === "retry") setTransient(sid, REASON_RETRY)
            break
          }

          case "session.idle":
            endOfTurn(sessionOf(p))
            break

          case "session.error":
            setTransient(sessionOf(p), REASON_ERROR)
            break

          case "message.updated": {
            const info = p.info
            if (!info) break
            if (info.role === "assistant" && typeof info.id === "string") {
              const sid = typeof info.sessionID === "string" ? info.sessionID : sessionOf(p)
              const cwd = (info.path && info.path.cwd) || ""
              msgModel.set(info.id, {
                providerID: info.providerID || "",
                modelID: info.modelID || "",
                sessionID: sid,
                cwd: cwd,
                project: cwd ? cwd.split(/[\\/]/).filter(Boolean).pop() : project(),
                providerCost: typeof info.cost === "number" ? info.cost : 0,
              })
              const entry = entryOf(sid)
              const meta = metaOf(sid)
              if (cwd) {
                entry.cwd = cwd
                entry.project = cwd.split(/[\\/]/).filter(Boolean).pop() || entry.project
                meta.cwd = cwd
                meta.project = entry.project
                stateDirty = true
              }
              if (info.providerID || info.modelID) {
                entry.lastModel = { providerID: info.providerID || "", modelID: info.modelID || "" }
              }
            }
            if (info.role === "user") {
              clearPending(sessionOf(info))
              setState(sessionOf(info), "working", "")
            } else if (info.error) {
              setTransient(sessionOf(info), REASON_ERROR)
            }
            break
          }

          case "message.part.updated": {
            const part = p.part
            if (!part) break
            const sid = typeof part.sessionID === "string" && part.sessionID.length > 0
              ? part.sessionID
              : sessionOf(p)
            if (part.type === "step-finish" && typeof part.id === "string") {
              if (!seenSteps.has(part.id)) {
                const tk = part.tokens || {}
                const cache = tk.cache || {}
                const model = modelOf(part.messageID || "", sid)
                // solo token opencode: openrouter e gli altri provider restano fuori
                if (model && isOpencodeProvider(model.providerID)) {
                  addStep(sid, model, tk, cache, typeof part.cost === "number" ? part.cost : 0)
                  seenSteps.set(part.id, { sid: sid, at: Date.now() })
                  stateDirty = true
                }
              }
              setActivity(sid, "modello")
            } else if (part.type === "tool") {
              const status = part.state && part.state.status
              if (status === "running" || status === "pending") {
                if (isQuestion(part.tool)) setPending(sid, REASON_QUESTION)
                else {
                  setActivity(sid, part.tool || "tool")
                  setState(sid, "working", "")
                }
              } else if (status === "error") {
                if (isQuestion(part.tool)) clearPending(sid)
                setTransient(sid, REASON_ERROR)
              } else if (status === "completed" && isQuestion(part.tool)) {
                clearPending(sid)
                setState(sid, "working", "")
              }
            } else if (part.type === "retry") {
              setTransient(sid, REASON_RETRY)
            } else if (part.type === "text" && part.time && part.time.start && !part.time.end) {
              setActivity(sid, "risposta")
              setState(sid, "working", "")
            }
            break
          }

          // opencode 1.18 usa il namespace v2: permission.asked (permission.updated
          // e' tenuto per compatibilita' con altre versioni)
          case "permission.asked":
          case "permission.updated":
            setPending(sessionOf(p), REASON_PERMISSION)
            break

          case "permission.replied":
            clearPending(sessionOf(p))
            setState(sessionOf(p), "working", "")
            break

          // domande all'utente (tool question): eventi dedicati del namespace v2
          case "question.asked":
          // opencode v2: il tool question apre un form e resta in attesa
          // finche' l'utente non risponde (form.replied) o annulla.
          case "form.created":
            setPending(sessionOf(p), REASON_QUESTION)
            break

          case "question.replied":
          case "question.rejected":
          case "form.replied":
          case "form.cancelled":
            clearPending(sessionOf(p))
            setState(sessionOf(p), "working", "")
            break
        }
      } catch {}
    },

    "tool.execute.before": async (input) => {
      try {
        lastEventAt = Date.now()
        const sid = sessionOf(input)
        const tool = (input && input.tool) || ""
        if (!seenTools.has(tool)) {
          seenTools.add(tool)
          await diag("TOOL " + tool)
        }
        if (isQuestion(tool)) setPending(sid, REASON_QUESTION)
        else {
          setActivity(sid, (input && input.tool) || "tool")
          setState(sid, "working", "")
        }
      } catch {}
    },

    "tool.execute.after": async (input) => {
      try {
        lastEventAt = Date.now()
        const sid = sessionOf(input)
        if (isQuestion(input && input.tool)) clearPending(sid)
        setState(sid, "working", "")
      } catch {}
    },

    "permission.ask": async (input) => {
      try {
        lastEventAt = Date.now()
        setPending(sessionOf(input), REASON_PERMISSION)
      } catch {}
    },
  }
  Object.defineProperty(handlers, "dispose", { value: () => clearInterval(heartbeat) })
  return handlers
}

export default {
  id: "opencode-tray",

  // OpenCode v2 plugin API. The legacy handlers are adapted to v2's event
  // stream and hook registration instead of being returned as a v1 plugin.
  async setup(ctx) {
    const handlers = await OpencodeTrayPlugin()
    const controller = new AbortController()

    void (async () => {
      try {
        for await (const event of ctx.event.subscribe({ signal: controller.signal })) {
          await handlers.event({ event })
        }
      } catch (error) {
        if (!controller.signal.aborted) console.error("opencode-tray event stream failed", error)
      }
    })()

    await ctx.tool.hook("execute.before", async (event) => {
      await handlers["tool.execute.before"]({
        tool: event.tool,
        sessionID: event.sessionID,
      })
    })
    await ctx.tool.hook("execute.after", async (event) => {
      await handlers["tool.execute.after"]({
        tool: event.tool,
        sessionID: event.sessionID,
      })
    })
    await ctx.permission.hook("evaluate", async (event) => {
      if (event.effect === "ask") {
        await handlers["permission.ask"]({ sessionID: event.sessionID })
      }
    })

    return () => {
      controller.abort()
      handlers.dispose()
    }
  },

  // Keep support for OpenCode v1 installations using the legacy plugin API.
  async server() {
    return OpencodeTrayPlugin()
  },
}
