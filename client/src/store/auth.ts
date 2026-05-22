import { create } from "zustand";

const readApiKey = (): string => {
    const fromWindow = (window as unknown as { shokoApiKey?: string }).shokoApiKey;
    if (fromWindow) return fromWindow;
    try {
        const session = localStorage.getItem("apiSession");
        if (session) return (JSON.parse(session).apikey as string) || "";
    } catch {
        /* ignore */
    }
    return "";
};

interface AuthState {
    apiKey: string;
    setApiKey: (key: string) => void;
}

export const useAuthStore = create<AuthState>((set) => ({
    apiKey: readApiKey(),
    setApiKey: (apiKey) => set({ apiKey })
}));
