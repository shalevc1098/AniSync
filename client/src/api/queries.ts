import { keepPreviousData, useMutation, useQuery, useQueryClient } from "@tanstack/react-query";
import { toast } from "sonner";
import { api } from "@/lib/api";
import {
    DashboardSchema,
    GlobalSettingsSchema,
    HistorySchema,
    UserSettingsSchema,
    type GlobalSettings,
    type Settings
} from "@/lib/schemas";

export const useDashboard = () =>
    useQuery({
        queryKey: ["dashboard"],
        queryFn: async () => DashboardSchema.parse((await api.get("/api/dashboard")).data)
    });

export const useUserSettings = () =>
    useQuery({
        queryKey: ["userSettings"],
        queryFn: async () => UserSettingsSchema.parse((await api.get("/api/user/settings")).data)
    });

export const useHistory = (limit?: number) =>
    useQuery({
        queryKey: ["history", limit ?? null],
        queryFn: async () =>
            HistorySchema.parse((await api.get("/api/history", { params: { limit } })).data),
        placeholderData: keepPreviousData
    });

export const useGlobalSettings = (enabled: boolean) =>
    useQuery({
        queryKey: ["globalSettings"],
        enabled,
        queryFn: async () =>
            GlobalSettingsSchema.parse((await api.get("/api/global-settings")).data)
    });

export const useSaveSettings = () => {
    const qc = useQueryClient();
    return useMutation({
        mutationFn: async (settings: Settings) => {
            await api.post("/Settings", {
                UpdateNsfw: settings.updateNsfw,
                EnableAutoSync: settings.enableAutoSync,
                SyncOnlyCompleted: settings.syncOnlyCompleted,
                SetStartDateFromAnyEpisode: settings.setStartDateFromAnyEpisode,
                EnableRewatchDetection: settings.enableRewatchDetection,
                AllowRollback: settings.allowRollback,
                TitleMatchThreshold: settings.titleMatchThreshold,
                UseFuzzyMatching: settings.useFuzzyMatching,
                SyncDelaySeconds: settings.syncDelaySeconds,
                EnableDebugLogging: settings.enableDebugLogging
            });
        },
        onSuccess: () => qc.invalidateQueries({ queryKey: ["userSettings"] }),
        onError: () => toast.error("Failed to save settings")
    });
};

export const useSaveGlobalSettings = () => {
    const qc = useQueryClient();
    return useMutation({
        mutationFn: async (creds: GlobalSettings) => {
            const { data } = await api.post("/api/global-settings", {
                MalClientId: creds.malClientId,
                MalClientSecret: creds.malClientSecret,
                AniListClientId: creds.aniListClientId,
                AniListClientSecret: creds.aniListClientSecret
            });
            return data as { success: boolean; reAuthRequired: boolean };
        },
        onSuccess: () => {
            qc.invalidateQueries({ queryKey: ["globalSettings"] });
            qc.invalidateQueries({ queryKey: ["dashboard"] });
            qc.invalidateQueries({ queryKey: ["userSettings"] });
        }
    });
};

export const useDisconnect = () => {
    const qc = useQueryClient();
    return useMutation({
        mutationFn: async (provider: string) => {
            await api.post("/Logout", null, { params: { provider } });
        },
        onSuccess: () => {
            qc.invalidateQueries({ queryKey: ["dashboard"] });
            qc.invalidateQueries({ queryKey: ["userSettings"] });
        },
        onError: () => toast.error("Failed to disconnect")
    });
};

export const buildAuthorizeUrl = async (provider: string): Promise<string> => {
    const { data } = await api.get("/buildAuthorizeRequestUrl", {
        params: { provider, baseUrl: window.location.origin },
        responseType: "text"
    });
    return data as string;
};
