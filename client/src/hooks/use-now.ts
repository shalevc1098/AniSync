import { useEffect, useState } from "react";

// Re-renders the caller on an interval so relative-time labels ("3m ago") stay fresh.
export const useNow = (intervalMs = 60_000) => {
    const [, setTick] = useState(0);
    useEffect(() => {
        const id = setInterval(() => setTick((t) => t + 1), intervalMs);
        return () => clearInterval(id);
    }, [intervalMs]);
};
