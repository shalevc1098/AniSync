import { providerColor } from "@/lib/format";
import { Badge } from "@/components/ui/badge";

export const ProviderBadge = ({ provider }: { provider: string }) => {
    const color = providerColor(provider);
    return (
        <Badge
            variant="outline"
            style={{
                color,
                borderColor: `color-mix(in oklch, ${color} 40%, transparent)`,
                backgroundColor: `color-mix(in oklch, ${color} 12%, transparent)`
            }}
        >
            {provider}
        </Badge>
    );
};
