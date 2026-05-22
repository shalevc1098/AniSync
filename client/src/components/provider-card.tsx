import { Link2, Unlink } from "lucide-react";
import { toast } from "sonner";
import { buildAuthorizeUrl, useDisconnect } from "@/api/queries";
import { PROVIDER_API, PROVIDER_LABELS } from "@/lib/format";
import type { ProviderStatus } from "@/lib/schemas";
import aniListLogo from "@/assets/anilist.png";
import malLogo from "@/assets/mal.svg";
import { Badge } from "@/components/ui/badge";
import { Button } from "@/components/ui/button";
import { Card, CardContent } from "@/components/ui/card";
import {
    AlertDialog,
    AlertDialogAction,
    AlertDialogCancel,
    AlertDialogContent,
    AlertDialogDescription,
    AlertDialogFooter,
    AlertDialogHeader,
    AlertDialogTitle,
    AlertDialogTrigger
} from "@/components/ui/alert-dialog";

export const ProviderCard = ({
    providerKey,
    status
}: {
    providerKey: string;
    status: ProviderStatus;
}) => {
    const disconnect = useDisconnect();
    const label = PROVIDER_LABELS[providerKey];
    const logo = providerKey === "mal" ? malLogo : aniListLogo;

    const connect = async () => {
        try {
            window.location.href = await buildAuthorizeUrl(PROVIDER_API[providerKey]);
        } catch {
            toast.error(`Failed to start ${label} connection`);
        }
    };

    return (
        <Card className="overflow-hidden">
            <CardContent className="flex items-center gap-4">
                <img
                    src={logo}
                    alt={label}
                    className="size-12 shrink-0 rounded-xl"
                    style={{ opacity: status.connected ? 1 : 0.55 }}
                />

                <div className="min-w-0 flex-1">
                    <div className="flex items-center gap-2">
                        <span className="font-semibold">{label}</span>
                        <Badge variant={status.connected ? "default" : "secondary"}>
                            {status.connected ? "Connected" : "Not connected"}
                        </Badge>
                    </div>
                    <p className="truncate text-sm text-muted-foreground">
                        {status.connected
                            ? status.username
                            : status.configured
                              ? "Ready to connect"
                              : "Not configured"}
                    </p>
                </div>

                {status.connected ? (
                    <AlertDialog>
                        <AlertDialogTrigger asChild>
                            <Button variant="outline" size="sm">
                                <Unlink className="size-4" />
                                Disconnect
                            </Button>
                        </AlertDialogTrigger>
                        <AlertDialogContent>
                            <AlertDialogHeader>
                                <AlertDialogTitle>Disconnect {label}?</AlertDialogTitle>
                                <AlertDialogDescription>
                                    This removes the stored token. You can reconnect at any time.
                                </AlertDialogDescription>
                            </AlertDialogHeader>
                            <AlertDialogFooter>
                                <AlertDialogCancel>Cancel</AlertDialogCancel>
                                <AlertDialogAction
                                    onClick={() =>
                                        disconnect.mutate(PROVIDER_API[providerKey], {
                                            onSuccess: () => toast.success(`Disconnected ${label}`)
                                        })
                                    }
                                >
                                    Disconnect
                                </AlertDialogAction>
                            </AlertDialogFooter>
                        </AlertDialogContent>
                    </AlertDialog>
                ) : (
                    status.configured && (
                        <Button size="sm" onClick={connect}>
                            <Link2 className="size-4" />
                            Connect
                        </Button>
                    )
                )}
            </CardContent>
        </Card>
    );
};
