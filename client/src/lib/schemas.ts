import { z } from "zod";

export const ProviderStatusSchema = z.object({
    connected: z.boolean(),
    username: z.string().nullable().optional(),
    configured: z.boolean()
});

export const ProvidersSchema = z.object({
    mal: ProviderStatusSchema,
    aniList: ProviderStatusSchema
});

export const DashboardSchema = z.object({
    isAuthenticated: z.boolean(),
    isAdmin: z.boolean(),
    shokoUsername: z.string(),
    providers: ProvidersSchema,
    syncedAnime: z.number(),
    totalAnime: z.number(),
    lastSync: z.string().nullable(),
    pendingUpdates: z.number()
});

export const SettingsSchema = z.object({
    updateNsfw: z.boolean(),
    enableAutoSync: z.boolean(),
    syncOnlyCompleted: z.boolean(),
    setStartDateFromAnyEpisode: z.boolean(),
    enableRewatchDetection: z.boolean(),
    allowRollback: z.boolean(),
    titleMatchThreshold: z.number().min(0).max(1),
    useFuzzyMatching: z.boolean(),
    syncDelaySeconds: z.number().int().min(0).max(300),
    enableDebugLogging: z.boolean()
});

export const UserSettingsSchema = z.object({
    isAuthenticated: z.boolean(),
    isAdmin: z.boolean(),
    shokoUsername: z.string(),
    providers: ProvidersSchema,
    settings: SettingsSchema
});

export const WhoamiSchema = z.object({
    Username: z.string(),
    Avatar: z.string().nullable().optional(),
    IsAdmin: z.boolean().optional()
});

export const GlobalSettingsSchema = z.object({
    malClientId: z.string(),
    malClientSecret: z.string(),
    aniListClientId: z.string(),
    aniListClientSecret: z.string()
});

export const HistoryEntrySchema = z.object({
    timestamp: z.string(),
    action: z.string(),
    anime_id: z.number().nullable(),
    anime_title: z.string().nullable(),
    anime_image: z.string().nullable(),
    episode_number: z.number().nullable(),
    status: z.string(),
    success: z.boolean(),
    message: z.string(),
    details: z.string().nullable(),
    provider: z
        .object({
            name: z.string(),
            username: z.string().nullable().optional()
        })
        .nullable()
});

export const HistorySchema = z.object({
    history: z.array(HistoryEntrySchema),
    total_syncs: z.number(),
    failed_syncs: z.number(),
    last_sync: z.string().nullable()
});

export type ProviderStatus = z.infer<typeof ProviderStatusSchema>;
export type Providers = z.infer<typeof ProvidersSchema>;
export type Dashboard = z.infer<typeof DashboardSchema>;
export type Settings = z.infer<typeof SettingsSchema>;
export type UserSettings = z.infer<typeof UserSettingsSchema>;
export type GlobalSettings = z.infer<typeof GlobalSettingsSchema>;
export type Whoami = z.infer<typeof WhoamiSchema>;
export type HistoryEntry = z.infer<typeof HistoryEntrySchema>;
export type History = z.infer<typeof HistorySchema>;
