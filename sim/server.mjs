// Battlegrounds combat simulator service for the GameSpeed overlay.
//
// Runs Firestone's full combat simulator (@firestone-hs/simulate-bgs-battle) —
// the one that plays out card scripts: deathrattles, reborn, divine shields,
// venom, avenge, trinkets. The overlay POSTs a BgsBattleInfo and gets the
// win / tie / loss percentages back.
//
// Protocol:
//   stdout  "PORT <n>\n"   — printed once the service is ready
//   GET     /health        — "ok"
//   POST    /simulate      — body: BgsBattleInfo JSON → SimulationResult JSON
//
// The card database ships gzipped next to this file as cards.json.gz. When it
// is missing, it is downloaded once from Firestone's mirrors and re-saved.
// Requests are serialized: the simulator is single-threaded anyway.

import { createServer } from 'node:http';
import { readFile, writeFile } from 'node:fs/promises';
import { gunzipSync, gzipSync } from 'node:zlib';
import { fileURLToPath } from 'node:url';
import { dirname, join } from 'node:path';

const here = dirname(fileURLToPath(import.meta.url));
const cardsFile = join(here, 'cards.json.gz');
const MIRRORS = [
    'https://static.firestoneapp.com/data/cards/no_audio/cards_enUS.gz.json',
    'https://static.zerotoheroes.com/data/cards/no_audio/cards_enUS.gz.json',
];

const say = (msg) => process.stderr.write(`[sim] ${msg}\n`);

async function loadCards() {
    try {
        const raw = gunzipSync(await readFile(cardsFile));
        return JSON.parse(raw.toString('utf8'));
    } catch { /* fall through to download */ }

    for (const url of MIRRORS) {
        try {
            say(`базы карт нет, скачиваю: ${url}`);
            const res = await fetch(url, { signal: AbortSignal.timeout(180_000) });
            if (!res.ok) throw new Error(`HTTP ${res.status}`);
            const buf = Buffer.from(await res.arrayBuffer());
            const cards = JSON.parse(buf.toString('utf8'));
            if (!Array.isArray(cards) || cards.length === 0) throw new Error('пустой ответ');
            await writeFile(cardsFile, gzipSync(buf, { level: 6 }));
            return cards;
        } catch (e) {
            say(`не вышло: ${e.message}`);
        }
    }
    throw new Error('базу карт загрузить не удалось');
}

const cards = new (await import('@firestone-hs/reference-data')).AllCardsService();
cards.initializeCardsDbFromCards(await loadCards());
const { simulateBattle, prepareBattleCardsData, SIMULATOR_VERSION } =
    await import('@firestone-hs/simulate-bgs-battle');
say(`карт: ${cards.getCards().length}, симулятор ${SIMULATOR_VERSION}`);

// One fight at a time; the rest queue up.
let queue = Promise.resolve();
const enqueue = (job) => {
    const run = queue.then(job, job);
    queue = run.catch(() => {});
    return run;
};

const CAPS = { numberOfSimulations: 10_000, maxAcceptableDuration: 2_000 };

function simulate(battle) {
    const options = { ...(battle.options ?? {}) };
    options.skipInfoLogs = true;
    options.hideMaxSimulationDurationWarning = true;
    options.includeOutcomeSamples = false;
    if (!(options.numberOfSimulations > 0)) options.numberOfSimulations = 2_000;
    options.numberOfSimulations = Math.min(options.numberOfSimulations, CAPS.numberOfSimulations);
    if (!(options.maxAcceptableDuration > 0)) options.maxAcceptableDuration = 500;
    options.maxAcceptableDuration = Math.min(options.maxAcceptableDuration, CAPS.maxAcceptableDuration);
    const input = { ...battle, options };
    const cardsData = prepareBattleCardsData(input, cards);
    return simulateBattle(input, cards, cardsData).next().value;
}

const server = createServer((req, res) => {
    if (req.method === 'GET' && req.url === '/health') {
        res.writeHead(200, { 'content-type': 'text/plain' }).end('ok');
        return;
    }
    if (req.method !== 'POST' || req.url !== '/simulate') {
        res.writeHead(404).end();
        return;
    }
    const chunks = [];
    let size = 0;
    req.on('data', (c) => {
        size += c.length;
        if (size > 8 * 1024 * 1024) req.destroy(new Error('too big'));
        else chunks.push(c);
    });
    req.on('error', () => res.writeHead(413).end());
    req.on('end', async () => {
        try {
            const battle = JSON.parse(Buffer.concat(chunks).toString('utf8'));
            const result = await enqueue(() => simulate(battle));
            res.writeHead(200, { 'content-type': 'application/json' }).end(JSON.stringify(result));
        } catch (e) {
            say(`ошибка расчёта: ${e?.message ?? e}`);
            if (!res.headersSent) res.writeHead(500, { 'content-type': 'text/plain' });
            res.end(String(e?.message ?? e));
        }
    });
});

server.listen(0, '127.0.0.1', () => {
    // The parent process reads this one line; keep stdout otherwise silent.
    process.stdout.write(`PORT ${server.address().port}\n`);
});

process.on('SIGTERM', () => process.exit(0));
process.on('SIGINT', () => process.exit(0));

// The overlay keeps our stdin open and never writes; it closing means the
// parent is gone (crash included), so there is no one left to answer.
process.stdin.resume();
process.stdin.on('end', () => process.exit(0));
