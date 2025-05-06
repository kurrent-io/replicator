import {type navItem} from "./types/nav.d";

const navConfig: navItem[] = [
    {
        text: "Platform",
        megaMenuColumns: [
            {
                type: "regular",
                title: "Kurrent Cloud",
                items: [
                    {
                        text: "Cloud Overview",
                        link: "https://www.kurrent.io/kurrent-cloud"
                    },
                    {
                        text: "Sign Up",
                        link: "https://console.kurrent.cloud/signup",
                        newTab: true
                    }
                ]
            },
            {
                type: "regular",
                title: "Kurrent",
                items: [
                    {
                        text: "Platform Overview",
                        link: "https://www.kurrent.io/kurrent"
                    },
                    {
                        text: "KurrentDB Editions",
                        link: "https://www.kurrent.io/kurrent-platform-editions"
                    }
                ]
            },
            
        ]
    },

    // mega menu
    {
        text: "Use Cases",
        megaMenuColumns: [
            {
                type: "cta",
                title: "Use Cases",
                buttonText: "Case studies",
                link: "https://www.kurrent.io/case-studies",
                text: "How customers around the world use Kurrent."
            },
            {
                type: "regular",
                title: "By Technology",
                items: [
                    {
                        text: "Kafka",
                        link: "https://www.kurrent.io/use-cases/supercharge-kafka-with-kurrent",
                    },
                    {
                        text: "Artificial Intelligence (AI)",
                        link: "https://www.kurrent.io/use-cases/ai-ml-systems",
                    },
                    {
                        text: "Machine Learning (ML)",
                        link: "https://www.kurrent.io/use-cases/unleash-the-power-of-ml-with-event-native-data",
                    },
                    {
                        text: "Application Development",
                        link: "https://www.kurrent.io/use-cases/streamline-application-development-with-kurrent",
                    },
                ],
            },
            {
                type: "regular",
                title: "By Industry",
                items: [
                    {
                        text: "Finance",
                        link: "https://www.kurrent.io/use-cases/finance",
                    },
                    {
                        text: "Healthcare",
                        link: "https://www.kurrent.io/use-cases/healthcare",
                    },
                    {
                        text: "Government",
                        link: "https://www.kurrent.io/use-cases/government",
                    },
                    {
                        text: "Retail",
                        link: "https://www.kurrent.io/use-cases/retail",
                    },
                    {
                        text: "Tech",
                        link: "https://www.kurrent.io/use-cases/technology",
                    },
                    {
                        text: "Logistics & Transportation",
                        link: "https://www.kurrent.io/use-cases/transport",
                    },
                ],
            },
        ],
    },
    {
        text: "Resources",
        megaMenuColumns: [
            {
                type: "regular",
                title: "Guides",
                items: [
                    {
                        text: "Event Sourcing",
                        link: "https://www.kurrent.io/event-sourcing"
                    },
                    {
                        text: "CQRS",
                        link: "https://www.kurrent.io/cqrs-pattern"
                    },
                    {
                        text: "Event-Driven Microservices",
                        link: "https://www.kurrent.io/event-driven-architecture"
                    },
                    // {
                    //     text: "Tutorials",
                    //     link: "/tutorials"
                    // }
                ]
            },
            {
                type: "regular",
                title: "Documentation",
                items: [
                    {
                        text: "KurrentDB",
                        link: "https://docs.kurrent.io/latest",
                        newTab: true
                    },
                    {
                        text: "Kurrent Cloud",
                        link: "https://docs.kurrent.io/cloud/introduction.html",
                        newTab: true
                    },
                    {
                        text: "Connectors",
                        link: "https://docs.kurrent.io/server/v25.0/features/connectors/",
                        newTab: true
                    },
                    {
                        text: "Client Libraries",
                        link: "https://docs.kurrent.io/clients/grpc/getting-started.html",
                        newTab: true
                    },
                ]
            },
            {
                type: "regular",
                title: "Connect",
                items: [
                    {
                        text: "Blog",
                        link: "https://www.kurrent.io/blog"
                    },
                    {
                        text: "Community",
                        link: "https://www.kurrent.io/community"
                    },
                    {
                        text: "Courses",
                        link: "https://academy.kurrent.io/"
                    },
                    {
                        text: "Events and Webinars",
                        link: "/events"
                    },
                    {
                        text: "FAQs",
                        link: "https://www.kurrent.io/faq"
                    },
                ]
            }
        ]
    },
    {
        text: "Services",
        megaMenuColumns: [
            {
                type: "cta",
                title: "Report an Issue",
                buttonText: "Report an Issue",
                link: "https://www.kurrent.io/support/report-an-issue",
                text: "Let us know what issue you are facing and we will do our best to help you."
            },
            // {
            //     type: "regular",
            //     title: "Support",
            //     items: [
            //         {
            //             text: "Customer Support",
            //             link: "https://www.kurrent.io/support"
            //         },
            //     ]
            // },
            {
                type: "regular",
                title: "Professional Services",
                items: [
                    // {
                    //     text: "Health Check",
                    //     link: "/training"
                    // },
                    // {
                    //     text: "Training",
                    //     link: "https://academy.kurrent.io/",
                    //     newTab: true
                    // },
                    {
                        text: "Consulting",
                        link: "https://www.kurrent.io/consulting"
                    },
                ]
            },
            {
                type: "regular",
                title: "Training",
                items: [
                    {
                        text: "Academy",
                        link: "https://academy.kurrent.io/",
                        newTab: true
                    },
                    {
                        text: "On-site Training",
                        link: "https://academy.kurrent.io/live-training",
                        newTab: true
                    }
                ]
            }
        ]
    },
    {
        text: "About",
        megaMenuColumns: [
            {
                type: "cta",
                title: "Contact Us",
                buttonText: "Get in touch",
                link: "https://www.kurrent.io/talk_to_expert",
                // image: cosmicLogo,
                text: "Want to get in touch with the Kurrent team? Visit our contact page."
            },
            {
                type: "regular",
                title: "Company",
                items: [
                    {
                        text: "Our Story",
                        link: "https://www.kurrent.io/about"
                    },
                    {
                        text: "Careers",
                        link: "https://www.kurrent.io/careers"
                    },
                    {
                        text: "Community",
                        link: "https://www.kurrent.io/community"
                    },
                    {
                        text: "Company News",
                        link: "https://www.kurrent.io/blog/news"
                    }
                ]
            }
        ]
    },
];

export default navConfig;