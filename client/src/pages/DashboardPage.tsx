import { Link } from "react-router-dom";
import { CheckCircle2, AlertTriangle } from "lucide-react";
import { toast } from "sonner";
import { useDashboard, buildAuthorizeUrl } from "@/api/queries";
import type { ProviderStatus } from "@/lib/schemas";
import { formatLastSync, PROVIDER_API, PROVIDER_LABELS } from "@/lib/format";
import { Alert, AlertDescription } from "@/components/ui/alert";
import { Button } from "@/components/ui/button";
import { Card, CardContent } from "@/components/ui/card";
import { Skeleton } from "@/components/ui/skeleton";

const connect = async (providerKey: string) => {
    try {
        const url = await buildAuthorizeUrl(PROVIDER_API[providerKey]);
        window.location.href = url;
    } catch {
        toast.error(`Failed to start ${PROVIDER_LABELS[providerKey]} connection`);
    }
};

const Stat = ({ label, value }: { label: string; value: string | number }) => (
    <Card>
        <CardContent>
            <div className="text-3xl font-bold text-primary">{value}</div>
            <div className="mt-1 text-sm uppercase tracking-wide text-muted-foreground">
                {label}
            </div>
        </CardContent>
    </Card>
);

const DashboardPage = () => {
    const { data, isLoading, isError } = useDashboard();

    if (isLoading) {
        return (
            <div className="space-y-4">
                <Skeleton className="h-14 w-full" />
                <div className="grid grid-cols-2 gap-4 sm:grid-cols-4">
                    {Array.from({ length: 4 }).map((_, i) => (
                        <Skeleton key={i} className="h-24 w-full" />
                    ))}
                </div>
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
    const connectable = entries.filter(([, p]) => !p.connected && p.configured);

    return (
        <div className="space-y-6">
            {data.isAuthenticated ? (
                <div className="space-y-3">
                    {entries
                        .filter(([, p]) => p.connected)
                        .map(([key, p]) => (
                            <Alert key={key}>
                                <CheckCircle2 className="text-green-500" />
                                <AlertDescription>
                                    Connected to {PROVIDER_LABELS[key]} as{" "}
                                    <strong className="text-foreground">{p.username}</strong>
                                </AlertDescription>
                            </Alert>
                        ))}
                </div>
            ) : (
                <Alert variant="destructive">
                    <AlertTriangle />
                    <AlertDescription>
                        No provider connected yet. Connect AniList and/or MyAnimeList to start
                        syncing.
                    </AlertDescription>
                </Alert>
            )}

            {(connectable.length > 0 || !anyConfigured) && (
                <div className="flex flex-wrap gap-2">
                    {connectable.map(([key]) => (
                        <Button key={key} variant="secondary" onClick={() => connect(key)}>
                            Connect {PROVIDER_LABELS[key]}
                        </Button>
                    ))}
                    {!anyConfigured && (
                        <p className="text-sm text-muted-foreground">
                            {data.isAdmin ? (
                                <>
                                    Add provider API credentials in{" "}
                                    <Link to="/settings" className="text-primary underline">
                                        Settings
                                    </Link>{" "}
                                    to enable connecting.
                                </>
                            ) : (
                                "Ask an administrator to configure API credentials."
                            )}
                        </p>
                    )}
                </div>
            )}

            {data.isAuthenticated && (
                <div className="grid grid-cols-2 gap-4 sm:grid-cols-4">
                    <Stat label="Total Anime" value={data.totalAnime} />
                    <Stat label="Synced" value={data.syncedAnime} />
                    <Stat label="Last Sync" value={formatLastSync(data.lastSync)} />
                    <Stat label="Pending Updates" value={data.pendingUpdates} />
                </div>
            )}
        </div>
    );
};

export default DashboardPage;
