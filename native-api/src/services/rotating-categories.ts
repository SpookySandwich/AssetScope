// Chase Freedom's rotating 5% categories, read from Chase's own quarterly
// press release. Chase posts each quarter at a fixed address about two weeks
// before it starts: the page title lists the picks after a colon, and the
// body names the activation deadline. Picks that are single merchants or
// charities have no spending category here, so they are kept as Chase words
// them and shown rather than scored.
import type { RewardCategory } from './rewards.js';

/** Where cardmembers activate each quarter's categories (named in every release). */
export const CHASE_ACTIVATION_URL = 'https://www.chasebonus.com/';

const FETCH_TIMEOUT_MS = 10_000;
const USER_AGENT = 'AssetScope (local personal finance app)';

export function quarterOf(iso: string): string {
  return `${iso.slice(0, 4)}-Q${Math.floor((Number(iso.slice(5, 7)) - 1) / 3) + 1}`;
}

export function nextQuarter(quarter: string): string {
  const year = Number(quarter.slice(0, 4));
  const q = Number(quarter.slice(6));
  return q === 4 ? `${year + 1}-Q1` : `${year}-Q${q + 1}`;
}

export function quarterStart(quarter: string): string {
  return `${quarter.slice(0, 4)}-${String(Number(quarter.slice(6)) * 3 - 2).padStart(2, '0')}-01`;
}

/** Chase has set each deadline on the 14th of the quarter's last month. */
export function usualActivateBy(quarter: string): string {
  return `${quarter.slice(0, 4)}-${String(Number(quarter.slice(6)) * 3).padStart(2, '0')}-14`;
}

export function chaseAnnouncementUrl(quarter: string): string {
  return `https://media.chase.com/news/chase-freedom-${quarter.slice(0, 4)}-q${quarter.slice(6)}-categories`;
}

export type ParsedAnnouncement = {
  categories: RewardCategory[];
  /** Picks with no spending category here, such as one store or a charity. */
  untracked: string[];
  activateBy: string;
};

export type AnnouncementCheck =
  | { state: 'read'; url: string; announcement: ParsedAnnouncement }
  | { state: 'notPosted' | 'unreadable'; url: string };

// First match wins, so Chase Travel (the card's own portal) never reads as travel.
const PICKS: [RegExp, RewardCategory | null][] = [
  [/\bchase travel\b/i, null],
  [/\b(grocery|groceries|supermarkets?)\b/i, 'groceries'],
  [/\b(dining|restaurants?)\b/i, 'dining'],
  [/\b(gas|ev charging|electric vehicle charging)\b/i, 'gas'],
  [/\btransit\b/i, 'transit'],
  [/\bstreaming\b/i, 'streaming'],
  [/\b(drugstores?|pharmac(y|ies))\b/i, 'drugstores'],
  [/\bhome improvement\b/i, 'homeImprovement'],
  [/\b(internet|cable|phone|wireless)\b/i, 'phoneInternet'],
  [/\b(live entertainment|movie theaters?)\b/i, 'entertainment'],
];

const MONTHS = ['january', 'february', 'march', 'april', 'may', 'june', 'july', 'august',
  'september', 'october', 'november', 'december'];

const DEADLINE_RES = [
  /\buntil\s+([a-z]+)\.?\s+(\d{1,2})(?:st|nd|rd|th)?,?\s+(\d{4}),?\s+to\s+activate/i,
  /\bactivate\b[^.]{0,80}?\bby\s+([a-z]+)\.?\s+(\d{1,2})(?:st|nd|rd|th)?,?\s+(\d{4})/i,
];

function decodeEntities(text: string): string {
  return text
    .replace(/&#x([0-9a-f]+);/gi, (_, hex: string) => String.fromCodePoint(parseInt(hex, 16)))
    .replace(/&#(\d+);/g, (_, dec: string) => String.fromCodePoint(Number(dec)))
    .replace(/&nbsp;/g, ' ')
    .replace(/&rsquo;|&lsquo;/g, "'")
    .replace(/&quot;/g, '"')
    .replace(/&lt;/g, '<')
    .replace(/&gt;/g, '>')
    .replace(/&amp;/g, '&')
    // Marks such as &reg; and &trade; carry no meaning here.
    .replace(/&[a-z]+;/gi, '');
}

function deadlineIn(text: string, quarter: string): string | null {
  for (const re of DEADLINE_RES) {
    const m = re.exec(text);
    const month = m ? MONTHS.indexOf(m[1].toLowerCase()) + 1 : 0;
    if (!m || month === 0) continue;
    const iso = `${m[3]}-${String(month).padStart(2, '0')}-${m[2].padStart(2, '0')}`;
    if (quarterOf(iso) === quarter && !Number.isNaN(Date.parse(iso))) return iso;
  }
  return null;
}

/** Reads one quarter's release, or null when the page is not laid out as expected. */
export function parseChaseAnnouncement(html: string, quarter: string): ParsedAnnouncement | null {
  const title = decodeEntities(/<title[^>]*>([\s\S]*?)<\/title>/i.exec(html)?.[1] ?? '').replace(/\s+/g, ' ').trim();
  const colon = title.indexOf(':');
  if (colon < 0 || !/freedom/i.test(title.slice(0, colon))) return null;

  const picks = title.slice(colon + 1)
    .replace(/\([^)]*\)/g, ' ')
    .replace(/[®™℠]/g, '')
    .split(/,|&|\band\b/i)
    .map((pick) => pick.replace(/\s+/g, ' ').trim())
    .filter(Boolean);
  if (picks.length === 0) return null;

  const categories: RewardCategory[] = [];
  const untracked: string[] = [];
  for (const pick of picks) {
    const category = PICKS.find(([re]) => re.test(pick))?.[1] ?? null;
    if (!category) untracked.push(pick);
    else if (!categories.includes(category)) categories.push(category);
  }

  const text = decodeEntities(html
    .replace(/<(script|style)\b[\s\S]*?<\/\1>/gi, ' ')
    .replace(/<[^>]+>/g, ' '))
    .replace(/\s+/g, ' ');
  return { categories, untracked, activateBy: deadlineIn(text, quarter) ?? usualActivateBy(quarter) };
}

/** Fetches one quarter's release. Network failures throw; a missing page is not an error. */
export async function fetchChaseAnnouncement(quarter: string, fetchImpl: typeof fetch = fetch): Promise<AnnouncementCheck> {
  const url = chaseAnnouncementUrl(quarter);
  const res = await fetchImpl(url, {
    headers: { 'User-Agent': USER_AGENT, Accept: 'text/html' },
    redirect: 'follow',
    signal: AbortSignal.timeout(FETCH_TIMEOUT_MS),
  });
  if (res.status === 404 || res.status === 410) return { state: 'notPosted', url };
  if (!res.ok) throw new Error(`Chase returned HTTP ${res.status}.`);
  // A retired address that now redirects elsewhere is not this quarter's release.
  if (res.url && new URL(res.url).pathname !== new URL(url).pathname) return { state: 'notPosted', url };
  const announcement = parseChaseAnnouncement(await res.text(), quarter);
  return announcement ? { state: 'read', url, announcement } : { state: 'unreadable', url };
}
