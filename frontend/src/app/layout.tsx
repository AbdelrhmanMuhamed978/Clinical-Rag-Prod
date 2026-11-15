import type { Metadata } from "next";
import { Inter } from "next/font/google";
import "./globals.css";
import { TooltipProvider } from "@/components/ui/tooltip";
import { Toaster } from "@/components/ui/sonner";
import { AuthProvider } from "@/providers/AuthProvider";
import { QueryProvider } from "@/providers/QueryProvider";
import { BackendOfflineBanner } from "@/components/backend-offline-banner";

const inter = Inter({
  subsets: ["latin"],
  variable: "--font-inter",
  display: "swap",
});

export const metadata: Metadata = { title: "Clinical RAG Platform", description: "Configurable clinical guideline assistant" };

export default function RootLayout({ children }: { children: React.ReactNode }) {
  return (
    <html lang="en" className={inter.variable}>
      <body className="font-sans antialiased">
        <QueryProvider>
          <AuthProvider>
            <TooltipProvider>
              <BackendOfflineBanner />
              {/* 3D Floating Background */}
              <div className="gradient-orb gradient-orb-1"></div>
              <div className="gradient-orb gradient-orb-2"></div>
              <div className="gradient-orb gradient-orb-3"></div>
              <script
                dangerouslySetInnerHTML={{
                  __html: `
                    document.addEventListener('visibilitychange', () => {
                      document.body.classList.toggle('tab-hidden', document.hidden);
                    });
                  `,
                }}
              />
              {children}
              <Toaster />
            </TooltipProvider>
          </AuthProvider>
        </QueryProvider>
      </body>
    </html>
  );
}
