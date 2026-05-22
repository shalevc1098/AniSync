import type { LucideIcon } from "lucide-react";
import { Card, CardContent } from "@/components/ui/card";

export const StatCard = ({
    label,
    value,
    icon: Icon
}: {
    label: string;
    value: string | number;
    icon: LucideIcon;
}) => (
    <Card>
        <CardContent className="flex items-start justify-between gap-2">
            <div className="min-w-0">
                <div className="truncate text-2xl font-bold">{value}</div>
                <div className="mt-1 text-xs uppercase tracking-wide text-muted-foreground">
                    {label}
                </div>
            </div>
            <Icon className="size-5 shrink-0 text-muted-foreground" />
        </CardContent>
    </Card>
);
