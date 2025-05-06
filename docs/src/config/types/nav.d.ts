export interface navLinkItem {
    text: string;
    link: string;
    newTab?: boolean; // adds target="_blank" rel="noopener noreferrer" to link
    icon?: string; // adds an icon to the left of the text
}

export interface navDropdownItem {
    text: string;
    dropdown: navLinkItem[];
}

export interface navMegaDropdownCtaColumn {
    title: string;
    type: "cta";
    image?: ImageMetadata;
    text: string;
    link: string;
    buttonText: string;
}

export interface navMegaDropdownRegularColumn {
    title: string;
    type: "regular";
    items: navLinkItem[];
}

export type navMegaDropdownColumn = navMegaDropdownRegularColumn | navMegaDropdownCtaColumn;

export interface navMegaDropdownItem {
    text: string;
    megaMenuColumns: navMegaDropdownColumn[];
}

export type navItem = navLinkItem | navDropdownItem | navMegaDropdownItem;
