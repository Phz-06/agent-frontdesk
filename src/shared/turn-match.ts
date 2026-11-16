// One turn, two records of it: the turn the console runs or follows (RunService) and the same
// turn as the desktop app writes it down. The server (a chat's history) and the page (the store)
// both have to tell that they are one, so the rule lives here, once.

/** What either record of a turn has to say for the two to be matched. */
export interface TurnMark { id?: string | null; nativeTurnId?: string | null | undefined; userPrompt?: string | null | undefined; timestamp?: string | number | null | undefined }

/** A time as either record gives it (ISO text, seconds or milliseconds), in milliseconds; 0 when it has none. */
export function stampOf(value: unknown): number {
  if (typeof value === 'number') return value < 1e12 ? value * 1000 : value;
  return value == null ? 0 : Date.parse(String(value)) || 0;
}

/**
 * Whether `native` (read from the desktop's record) is the turn `run` (the console's) stands for.
 * By id once the engine has named the desktop's turn. Until then by the message: the desktop's
 * prompt begins with what the person typed (an engine may add to it, e.g. the paths of the
 * attached files), and was written no earlier than the moment the person sent it (`slackMs`
 * allows for the two clocks being read at different points).
 */
export function sameTurn(native: TurnMark, run: TurnMark, slackMs = 5000): boolean {
  if (native.id != null && (native.id === run.id || native.id === run.nativeTurnId)) return true;
  const typed = (run.userPrompt || '').trim(), written = (native.userPrompt || '').trim();
  if (!typed || !written.startsWith(typed)) return false;
  return stampOf(native.timestamp) >= stampOf(run.timestamp) - slackMs;
}
