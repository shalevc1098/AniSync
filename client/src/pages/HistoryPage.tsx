import { useHistory } from "@/api/queries";
import { useAuthStore } from "@/store/auth";
import { Alert, AlertDescription } from "@/components/ui/alert";
import { Badge } from "@/components/ui/badge";
import { Skeleton } from "@/components/ui/skeleton";
import {
    Table,
    TableBody,
    TableCell,
    TableHead,
    TableHeader,
    TableRow
} from "@/components/ui/table";

const HistoryPage = () => {
    const { data, isLoading, isError } = useHistory(100);
    const apiKey = useAuthStore((s) => s.apiKey);

    const thumbUrl = (path: string | null) =>
        path
            ? `${path}${path.includes("?") ? "&" : "?"}apikey=${encodeURIComponent(apiKey)}`
            : null;

    if (isLoading) return <Skeleton className="h-64 w-full" />;

    if (isError || !data) {
        return (
            <Alert variant="destructive">
                <AlertDescription>Couldn't load sync history.</AlertDescription>
            </Alert>
        );
    }

    if (data.history.length === 0) {
        return (
            <Alert>
                <AlertDescription>No sync activity yet.</AlertDescription>
            </Alert>
        );
    }

    return (
        <Table>
            <TableHeader>
                <TableRow>
                    <TableHead className="w-12"></TableHead>
                    <TableHead>When</TableHead>
                    <TableHead>Anime</TableHead>
                    <TableHead>Action</TableHead>
                    <TableHead>Provider</TableHead>
                    <TableHead>Result</TableHead>
                </TableRow>
            </TableHeader>
            <TableBody>
                {data.history.map((entry, i) => {
                    const thumb = thumbUrl(entry.anime_image);
                    return (
                        <TableRow key={i}>
                            <TableCell>
                                {thumb ? (
                                    <img
                                        src={thumb}
                                        alt=""
                                        loading="lazy"
                                        className="h-12 w-9 rounded object-cover"
                                    />
                                ) : (
                                    <div className="h-12 w-9 rounded bg-muted" />
                                )}
                            </TableCell>
                            <TableCell className="whitespace-nowrap text-muted-foreground">
                                {new Date(entry.timestamp).toLocaleString()}
                            </TableCell>
                            <TableCell className="font-medium">
                                {entry.anime_title ?? "Unknown"}
                                {entry.episode_number != null && (
                                    <span className="text-muted-foreground">
                                        {" "}
                                        (ep {entry.episode_number})
                                    </span>
                                )}
                            </TableCell>
                            <TableCell>{entry.action}</TableCell>
                            <TableCell>{entry.provider?.name ?? "-"}</TableCell>
                            <TableCell>
                                <Badge variant={entry.success ? "default" : "destructive"}>
                                    {entry.success ? "OK" : "Failed"}
                                </Badge>
                            </TableCell>
                        </TableRow>
                    );
                })}
            </TableBody>
        </Table>
    );
};

export default HistoryPage;
