export function parseLog(text) {
    const entries = [], unparsed = [];
    let current;
    const finish = () => {
        if (current) entries.push({ ...current, message: current.lines.join('\n').trimEnd(), lines: undefined });
        current = undefined;
    };
    for (const [index, line] of text.replace(/^\uFEFF/, '').split(/\r?\n/).entries()) {
        const header = line.match(/^\[(\d{2}:\d{2}:\d{2}\.\d{3})\] \[(DBG|INF|WRN|ERR|FTL|VRB)\] (?:\[([^\]]+)\] )?(.+?)(?: logQueueMs=[\d.]+)?$/);
        if (header) {
            finish();
            current = { line: index + 1, time: header[1], level: header[2], run: header[3] ?? 'legacy', logger: header[4], lines: [] };
        } else if (/^\[\d{2}:\d{2}:\d{2}/.test(line)) {
            finish();
            unparsed.push({ line: index + 1, text: line });
        } else if (current) current.lines.push(line);
        else if (line.trim()) unparsed.push({ line: index + 1, text: line });
    }
    finish();
    return { entries, unparsed };
}

export function summarizeLog(parsed) {
    const groups = new Map();
    let warningErrors = 0, lowLevelFailures = 0;
    for (const entry of parsed.entries) {
        const warningError = ['WRN', 'ERR', 'FTL'].includes(entry.level);
        const failure = /EVIDENCE_CAPTURE_MISSING|\b(?:System\.)?\w*Exception\b|(?:outcome|result|status)="?(?:failed|Failed|Unconfirmed)\b/.test(entry.message);
        if (!warningError && !failure) continue;
        if (warningError) warningErrors++; else lowLevelFailures++;
        const first = entry.message.split('\n')[0];
        const signature = first.replace(/\b[0-9a-f]{8}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{12}\b/gi, '<id>')
            .replace(/\b[0-9a-f]{32}\b/gi, '<id>');
        const key = JSON.stringify([entry.run, entry.level, entry.logger, signature]);
        if (!groups.has(key)) groups.set(key, { run: entry.run, level: entry.level, logger: entry.logger, signature, count: 0, occurrences: [] });
        const group = groups.get(key);
        group.count++;
        group.occurrences.push({ line: entry.line, time: entry.time, message: entry.message });
    }
    return { entryCount: parsed.entries.length, warningErrors, lowLevelFailures, groups: [...groups.values()], unparsed: parsed.unparsed };
}
import fs from 'node:fs';
import path from 'node:path';
import { pathToFileURL } from 'node:url';


if (process.argv[1] && import.meta.url === pathToFileURL(path.resolve(process.argv[1])).href) {
    if (!process.argv[2]) throw new Error('Usage: node Analyze-OneDragonLog.mjs <log-file>');
    const source = path.resolve(process.argv[2]);
    const bytes = fs.readFileSync(source);
    process.stdout.write(JSON.stringify({ source, bytesRead: bytes.length, ...summarizeLog(parseLog(bytes.toString('utf8'))) }, null, 2) + '\n');
}
