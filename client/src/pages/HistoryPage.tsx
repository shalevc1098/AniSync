import { Fragment, useMemo, useState } from "react";
import { CircleCheck, CircleX, Inbox } from "lucide-react";
import { useHistory } from "@/api/queries";
import { useAuthStore } from "@/store/auth";
import { useNow } from "@/hooks/use-now";
import { formatRelative } from "@/lib/format";
import { ProviderBadge } from "@/components/provider-badge";
import { Alert, AlertDescription } from "@/components/ui/alert";
import { Button } from "@/components/ui/button";
import { Card, CardContent, CardHeader, CardTitle } from "@/components/ui/card";
import { Skeleton } from "@/components/ui/skeleton";
import { Tabs, TabsList, TabsTrigger } from "@/components/ui/tabs";
import {
    Table,
    TableBody,
    TableCell,
    TableHead,
    TableHeader,
    TableRow
} from "@/components/ui/table";

const PAGE = 100;

const dateLabel = (iso: string) => {
    const d = new Date(iso);
    const start = new Date();
    start.setHours(0, 0, 0, 0);
    const that = new Date(d);
    that.setHours(0, 0, 0, 0);
    const diff = Math.round((start.getTime() - that.getTime()) / 86_400_000);
    if (diff <= 0) return "Today";
    if (diff === 1) return "Yesterday";
    if (diff < 7) return d.toLocaleDateString(undefined, { weekday: "long" });
    return d.toLocaleDateString(undefined, { year: "numeric", month: "long", day: "numeric" });
};

const HistoryPage = () => {
    const [limit, setLimit] = useState(PAGE);
    const { data, isLoading, isError } = useHistory(limit);
    const apiKey = useAuthStore((s) => s.apiKey);
    const [filter, setFilter] = useState("all");
    useNow();

    const thumb = (path: string | null) =>
        path
            ? `${path}${path.includes("?") ? "&" : "?"}apikey=${encodeURIComponent(apiKey)}`
            : null;

    const rows = useMemo(() => {
        const all = data?.history ?? [];
        if (filter === "all") return all;
        if (filter === "failed") return all.filter((e) => !e.success);
        return all.filter((e) => e.provider?.name === filter);
    }, [data, filter]);

    if (isLoading) return <Skeleton className="h-96 w-full" />;

    if (isError || !data) {
        return (
            <Alert variant="destructive">
                <AlertDescription>Couldn't load sync history.</AlertDescription>
            </Alert>
        );
    }

    const canLoadMore = data.history.length >= limit;

    return (
        <Card>
            <CardHeader className="flex flex-row items-center justify-between gap-4">
                <CardTitle className="text-base">
                    Sync history
                    <span className="ml-2 font-normal text-muted-foreground">
                        {data.total_syncs} total
                        {data.failed_syncs > 0 && `, ${data.failed_syncs} failed`}
                    </span>
                </CardTitle>
                <Tabs value={filter} onValueChange={setFilter}>
                    <TabsList>
                        <TabsTrigger value="all">All</TabsTrigger>
                        <TabsTrigger value="AniList">AniList</TabsTrigger>
                        <TabsTrigger value="Mal">MAL</TabsTrigger>
                        <TabsTrigger value="failed">Failed</TabsTrigger>
                    </TabsList>
                </Tabs>
            </CardHeader>
            <CardContent>
                {rows.length === 0 ? (
                    <div className="flex flex-col items-center gap-2 py-12 text-muted-foreground">
                        <Inbox className="size-8" />
                        <p className="text-sm">Nothing to show here.</p>
                    </div>
                ) : (
                    <Table>
                        <TableHeader>
                            <TableRow>
                                <TableHead className="w-12"></TableHead>
                                <TableHead>Anime</TableHead>
                                <TableHead>Action</TableHead>
                                <TableHead>Provider</TableHead>
                                <TableHead className="text-right">When</TableHead>
                                <TableHead className="w-16 text-right">Result</TableHead>
                            </TableRow>
                        </TableHeader>
                        <TableBody>
                            {rows.map((e, i) => {
                                const src = thumb(e.anime_image);
                                const label = dateLabel(e.timestamp);
                                const showHeader =
                                    i === 0 || label !== dateLabel(rows[i - 1].timestamp);
                                return (
                                    <Fragment key={`${e.timestamp}-${e.anime_id}-${i}`}>
                                        {showHeader && (
                                            <TableRow className="hover:bg-transparent">
                                                <TableCell
                                                    colSpan={6}
                                                    className="bg-muted/40 py-1.5 text-xs font-medium uppercase tracking-wide text-muted-foreground"
                                                >
                                                    {label}
                                                </TableCell>
                                            </TableRow>
                                        )}
                                        <TableRow>
                                            <TableCell>
                                                {src ? (
                                                    <img
                                                        src={src}
                                                        alt=""
                                                        loading="lazy"
                                                        className="h-12 w-9 rounded object-cover"
                                                    />
                                                ) : (
                                                    <div className="h-12 w-9 rounded bg-muted" />
                                                )}
                                            </TableCell>
                                            <TableCell className="font-medium">
                                                {e.anime_title ?? "Unknown"}
                                                {e.episode_number != null && (
                                                    <span className="font-normal text-muted-foreground">
                                                        {" "}
                                                        (ep {e.episode_number})
                                                    </span>
                                                )}
                                            </TableCell>
                                            <TableCell className="text-muted-foreground">
                                                {e.action}
                                            </TableCell>
                                            <TableCell>
                                                {e.provider?.name && (
                                                    <ProviderBadge provider={e.provider.name} />
                                                )}
                                            </TableCell>
                                            <TableCell
                                                className="whitespace-nowrap text-right text-muted-foreground"
                                                title={new Date(e.timestamp).toLocaleString()}
                                            >
                                                {formatRelative(e.timestamp)}
                                            </TableCell>
                                            <TableCell className="text-right">
                                                {e.success ? (
                                                    <CircleCheck className="ml-auto size-4 text-success" />
                                                ) : (
                                                    <CircleX className="ml-auto size-4 text-destructive" />
                                                )}
                                            </TableCell>
                                        </TableRow>
                                    </Fragment>
                                );
                            })}
                        </TableBody>
                    </Table>
                )}

                {canLoadMore && (
                    <div className="mt-4 flex justify-center">
                        <Button variant="outline" onClick={() => setLimit((l) => l + PAGE)}>
                            Load more
                        </Button>
                    </div>
                )}
            </CardContent>
        </Card>
    );
};

export default HistoryPage;
