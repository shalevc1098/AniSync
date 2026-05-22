export const formatLastSync = (iso: string | null): string => {
    if (!iso) return "Never";
    const date = new Date(iso);
    const mins = Math.floor((Date.now() - date.getTime()) / 60_000);
    if (mins < 1) return "Just now";
    if (mins < 60) return `${mins}m ago`;
    const hrs = Math.floor(mins / 60);
    if (hrs < 24) return `${hrs}h ago`;
    const days = Math.floor(hrs / 24);
    if (days < 7) return `${days}d ago`;
    return date.toLocaleDateString(undefined, { year: "numeric", month: "short", day: "2-digit" });
};

export const formatRelative = (iso: string): string => {
    const date = new Date(iso);
    const mins = Math.floor((Date.now() - date.getTime()) / 60_000);
    if (mins < 1) return "just now";
    if (mins < 60) return `${mins}m ago`;
    const hrs = Math.floor(mins / 60);
    if (hrs < 24) return `${hrs}h ago`;
    const days = Math.floor(hrs / 24);
    if (days < 7) return `${days}d ago`;
    return date.toLocaleDateString(undefined, { month: "short", day: "2-digit" });
};

export const providerColor = (provider: string): string => {
    switch (provider.toLowerCase()) {
        case "anilist":
            return "oklch(0.68 0.16 242)";
        case "mal":
            return "oklch(0.5 0.13 264)";
        default:
            return "var(--muted-foreground)";
    }
};

export const PROVIDER_LABELS: Record<string, string> = {
    aniList: "AniList",
    mal: "MyAnimeList"
};

export const PROVIDER_SHORT: Record<string, string> = {
    aniList: "AL",
    mal: "MAL"
};

export const PROVIDER_API: Record<string, string> = {
    aniList: "AniList",
    mal: "Mal"
};
