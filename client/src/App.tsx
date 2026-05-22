import { NavLink, Route, Routes } from "react-router-dom";
import { LayoutDashboard, Settings2, History as HistoryIcon } from "lucide-react";
import { cn } from "@/lib/utils";
import { ModeToggle } from "@/components/mode-toggle";
import DashboardPage from "@/pages/DashboardPage";
import SettingsPage from "@/pages/SettingsPage";
import HistoryPage from "@/pages/HistoryPage";

const navItems = [
    { to: "/", label: "Dashboard", icon: LayoutDashboard, end: true },
    { to: "/settings", label: "Settings", icon: Settings2, end: false },
    { to: "/history", label: "History", icon: HistoryIcon, end: false }
];

const App = () => (
    <div className="w-full px-6 py-8 lg:px-10">
        <header className="mb-8">
            <div className="mb-4 flex items-center justify-between">
                <h1 className="text-2xl font-bold tracking-tight">AniSync</h1>
                <ModeToggle />
            </div>
            <nav className="flex gap-1 border-b">
                {navItems.map(({ to, label, icon: Icon, end }) => (
                    <NavLink
                        key={to}
                        to={to}
                        end={end}
                        className={({ isActive }) =>
                            cn(
                                "flex items-center gap-2 border-b-2 px-4 py-2 text-sm font-medium transition-colors",
                                isActive
                                    ? "border-primary text-foreground"
                                    : "border-transparent text-muted-foreground hover:text-foreground"
                            )
                        }
                    >
                        <Icon className="size-4" />
                        {label}
                    </NavLink>
                ))}
            </nav>
        </header>

        <main>
            <Routes>
                <Route path="/" element={<DashboardPage />} />
                <Route path="/settings" element={<SettingsPage />} />
                <Route path="/history" element={<HistoryPage />} />
            </Routes>
        </main>
    </div>
);

export default App;
