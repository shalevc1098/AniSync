import { useMemo, useState } from "react";
import { CircleCheck, CircleX } from "lucide-react";
import { useHistory } from "@/api/queries";
import { useAuthStore } from "@/store/auth";
import { formatRelative } from "@/lib/format";
import { Alert, AlertDescription } from "@/components/ui/alert";
import { Badge } from "@/components/ui/badge";
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

const HistoryPage = () => {
    const { data, isLoading, isError } = useHistory(200);
    const apiKey = useAuthStore((s) => s.apiKey);
    const [filter, setFilter] = useState("all");

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
                    <p className="py-10 text-center text-sm text-muted-foreground">
                        Nothing to show here.
                    </p>
                ) : (
                    <Table>
                        <TableHeader>
                            <TableRow>
                                <TableHead className="w-12"></TableHead>
                                <TableHead>Anime</TableHead>
                                <TableHead>Action</TableHead>
                                <TableHead>Provider</TableHead>
                                <TableHead>When</TableHead>
                                <TableHead className="w-16 text-right">Result</TableHead>
                            </TableRow>
                        </TableHeader>
                        <TableBody>
                            {rows.map((e, i) => {
                                const src = thumb(e.anime_image);
                                return (
                                    <TableRow key={i}>
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
                                                <Badge variant="outline">{e.provider.name}</Badge>
                                            )}
                                        </TableCell>
                                        <TableCell
                                            className="whitespace-nowrap text-muted-foreground"
                                            title={new Date(e.timestamp).toLocaleString()}
                                        >
                                            {formatRelative(e.timestamp)}
                                        </TableCell>
                                        <TableCell className="text-right">
                                            {e.success ? (
                                                <CircleCheck className="ml-auto size-4 text-muted-foreground" />
                                            ) : (
                                                <CircleX className="ml-auto size-4 text-destructive" />
                                            )}
                                        </TableCell>
                                    </TableRow>
                                );
                            })}
                        </TableBody>
                    </Table>
                )}
            </CardContent>
        </Card>
    );
};

export default HistoryPage;
