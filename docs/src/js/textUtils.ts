/**
 * * returns "slugified" text.
 * @param text
 */
export function slugify(text: string): string {
	return text
		.toString()
		.toLowerCase() // convert to lowercase
		.replace(/\s+/g, "-") // replace spaces with -
		.replace(/[^\w-]+/g, "") // remove all non-word chars
		.replace(/--+/g, "-") // replace multiple dashes with single dash
		.replace(/^-+/, "") // trim dash from start of text
		.replace(/-+$/, ""); // trim dash from end of text
}

/**
 * * returns "humanized" text. runs slugify() and then replaces - with space and upper case first letter of every word, and lower case the rest
 * @param text
 */
export function humanize(text: string): string {
	const slugifiedText = slugify(text);
	return (
		slugifiedText
			.replace(/-/g, " ") // replace "-" with space
			// .toLowerCase();
			.replace(
				// upper case first letter of every word, and lower case the rest
				/\w\S*/g,
				(w) => w.charAt(0).toUpperCase() + w.slice(1).toLowerCase(),
			)
	);
}

// --------------------------------------------------------
/**
 * * returns "categorified" text. runs slugify() and then replaces - with space and upper cases everything
 * @returns string - categorified text
 * @param text
 */
export function categorify(text: string): string {
	const slugifiedText = slugify(text);
	return slugifiedText
		.replace(/-/g, " ") // replace "-" with space
		.toUpperCase();
}
