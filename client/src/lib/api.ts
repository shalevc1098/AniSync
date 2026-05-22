import axios from "axios";
import { useAuthStore } from "@/store/auth";

export const api = axios.create({ baseURL: "/anisync" });

api.interceptors.request.use((config) => {
    const key = useAuthStore.getState().apiKey;
    if (key) config.headers.set("apikey", key);
    return config;
});
