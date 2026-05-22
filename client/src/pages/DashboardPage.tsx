import { Link } from "react-router-dom";
import {
    AlertTriangle,
    CircleCheck,
    CircleX,
    Clock,
    Inbox,
    Library,
    ListChecks,
    RefreshCw
} from "lucide-react";
import { useDashboard, useHistory } from "@/api/queries";
import { useAuthStore } from "@/store/auth";
import type { ProviderStatus } from "@/lib/schemas";
import { formatLastSync, formatRelative } from "@/lib/format";
import { ProviderCard } from "@/components/provider-card";
import { ProviderBadge } from "@/components/provider-badge";
import { StatCard } from "@/components/stat-card";
import { Alert, AlertDescription } from "@/components/ui/alert";
import { Card, CardContent, CardHeader, CardTitle } from "@/components/ui/card";
import { Skeleton } from "@/components/ui/skeleton";

const RecentActivity = () => {
    const { data, isLoading } = useHistory(6);
    const apiKey = useAuthStore((s) => s.apiKey);
    const thumb = (path: string | null) =>
        path
            ? `${path}${path.includes("?") ? "&" : "?"}apikey=${encodeURIComponent(apiKey)}`
            : null;

    return (
        <Card>
            <CardHeader>
                <CardTitle className="text-base">Recent activity</CardTitle>
            </CardHeader>
            <CardContent className="space-y-1">
                {isLoading && <Skeleton className="h-40 w-full" />}
                {!isLoading && (!data || data.history.length === 0) && (
                    <div className="flex flex-col items-center gap-2 py-8 text-muted-foreground">
                        <Inbox className="size-7" />
                        <p className="text-sm">No sync activity yet.</p>
                    </div>
                )}
                {data?.history.map((e, i) => {
                    const src = thumb(e.anime_image);
                    return (
                        <div
                            key={i}
                            className="flex items-center gap-3 rounded-lg px-2 py-2 hover:bg-muted/50"
                        >
                            {src ? (
                                <img
                                    src={src}
                                    alt=""
                                    loading="lazy"
                                    className="h-12 w-9 shrink-0 rounded object-cover"
                                />
                            ) : (
                                <div className="h-12 w-9 shrink-0 rounded bg-muted" />
                            )}
                            <div className="min-w-0 flex-1">
                                <p className="truncate text-sm font-medium">
                                    {e.anime_title ?? "Unknown"}
                                    {e.episode_number != null && (
                                        <span className="font-normal text-muted-foreground">
                                            {" "}
                                            (ep {e.episode_number})
                                        </span>
                                    )}
                                </p>
                                <p className="text-xs text-muted-foreground">
                                    {e.action} {formatRelative(e.timestamp)}
                                </p>
                            </div>
                            {e.provider?.name && (
                                <span className="shrink-0">
                                    <ProviderBadge provider={e.provider.name} />
                                </span>
                            )}
                            {e.success ? (
                                <CircleCheck className="size-4 shrink-0 text-success" />
                            ) : (
                                <CircleX className="size-4 shrink-0 text-destructive" />
                            )}
                        </div>
                    );
                })}
            </CardContent>
        </Card>
    );
};

const DashboardPage = () => {
    const { data, isLoading, isError } = useDashboard();

    if (isLoading) {
        return (
            <div className="space-y-6">
                <div className="grid gap-4 sm:grid-cols-2">
                    <Skeleton className="h-20 w-full" />
                    <Skeleton className="h-20 w-full" />
                </div>
                <div className="grid grid-cols-2 gap-4 lg:grid-cols-4">
                    {Array.from({ length: 4 }).map((_, i) => (
                        <Skeleton key={i} className="h-24 w-full" />
                    ))}
                </div>
                <Skeleton className="h-64 w-full" />
            </div>
        );
    }

    if (isError || !data) {
        return (
            <Alert variant="destructive">
                <AlertTriangle />
                <AlertDescription>
                    Couldn't load the dashboard. Log in to Shoko Server and try again.
                </AlertDescription>
            </Alert>
        );
    }

    const entries = Object.entries(data.providers) as [string, ProviderStatus][];
    const anyConfigured = entries.some(([, p]) => p.configured);

    return (
        <div className="space-y-8">
            <section className="space-y-3">
                <div className="grid gap-4 sm:grid-cols-2">
                    {entries.map(([key, status]) => (
                        <ProviderCard key={key} providerKey={key} status={status} />
                    ))}
                </div>
                {!anyConfigured && (
                    <p className="text-sm text-muted-foreground">
                        {data.isAdmin ? (
                            <>
                                Add provider API credentials in{" "}
                                <Link to="/settings" className="font-medium underline">
                                    Settings
                                </Link>{" "}
                                to enable connecting.
                            </>
                        ) : (
                            "Ask an administrator to configure API credentials."
                        )}
                    </p>
                )}
            </section>

            {data.isAuthenticated && (
                <>
                    <section className="grid grid-cols-2 gap-4 lg:grid-cols-4">
                        <StatCard label="Total Anime" value={data.totalAnime} icon={Library} />
                        <StatCard label="Synced" value={data.syncedAnime} icon={ListChecks} />
                        <StatCard
                            label="Last Sync"
                            value={formatLastSync(data.lastSync)}
                            icon={Clock}
                        />
                        <StatCard label="Pending" value={data.pendingUpdates} icon={RefreshCw} />
                    </section>

                    <section>
                        <RecentActivity />
                    </section>
                </>
            )}
        </div>
    );
};

export default DashboardPage;
