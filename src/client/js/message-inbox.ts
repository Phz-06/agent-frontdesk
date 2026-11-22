export class MessageInbox<T extends { id: string } = { id: string }> {
  seen = new Set<string>(); initialized = false;
  ingest(notices: T[] = []): T[] {
    const fresh: T[] = [];
    for (const notice of notices) {
      if (this.seen.has(notice.id)) continue;
      this.seen.add(notice.id);
      if (this.initialized) fresh.push(notice);
    }
    this.initialized = true;
    while (this.seen.size > 400) { const oldest = this.seen.values().next().value; if (oldest === undefined) break; this.seen.delete(oldest); }
    return fresh;
  }
}
// A Codex directive (`:codex-file-citation{…}`, `::git-commit{…}`, 【a.ts†L3】) is drawn in a reply, never read out as text.
export function withoutDirectives(text: string): string {
  return text.replace(/(^|[^\w:]):{1,2}[A-Za-z][\w-]*(?:\[[^\]\n]*\])?\{(?:[^"'{}\n]|"(?:\\.|[^"\\\n])*"|'(?:\\.|[^'\\\n])*')*\}/g, '$1').replace(/【[^†】\n]+†L\d+(?:-L\d+)?】/g, '');
}
export function previewText(text: unknown, length = 160): string {
  const plain = withoutDirectives(String(text || '')).replace(/\[([^\]]+)\]\([^)]*\)/g, '$1').replace(/[*`#>]/g, '').replace(/\s+/g, ' ').trim();
  const chars = Array.from(plain);
  return chars.slice(0, length).join('') + (chars.length > length ? '…' : '');
}
