import type { HistoryEntry } from "@/lib/schemas";

export interface GroupedEntry {
    timestamp: string;
    action: string;
    anime_title: string | null;
    anime_image: string | null;
    episode_number: number | null;
    providers: { name: string; success: boolean }[];
    allSuccess: boolean;
}

const PROVIDER_ORDER = ["anilist", "mal"];
const providerRank = (name: string) => {
    const i = PROVIDER_ORDER.indexOf(name.toLowerCase());
    return i === -1 ? PROVIDER_ORDER.length : i;
};

export const groupHistory = (entries: HistoryEntry[]): GroupedEntry[] => {
    const groups: GroupedEntry[] = [];
    const byId = new Map<string, GroupedEntry>();

    for (const e of entries) {
        const provider = e.provider?.name;
        const existing = e.event_id ? byId.get(e.event_id) : undefined;
        if (existing) {
            if (provider && !existing.providers.some((p) => p.name === provider)) {
                existing.providers.push({ name: provider, success: e.success });
                existing.allSuccess = existing.allSuccess && e.success;
            }
            continue;
        }
        const group: GroupedEntry = {
            timestamp: e.timestamp,
            action: e.action,
            anime_title: e.anime_title,
            anime_image: e.anime_image,
            episode_number: e.episode_number,
            providers: provider ? [{ name: provider, success: e.success }] : [],
            allSuccess: e.success
        };
        groups.push(group);
        if (e.event_id) byId.set(e.event_id, group);
    }

    for (const g of groups) g.providers.sort((a, b) => providerRank(a.name) - providerRank(b.name));
    return groups;
};
