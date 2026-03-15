from bs4 import BeautifulSoup
import json, sys

html = sys.stdin.read()

# Split by the separator
chunks = html.split('---ITEM---')

results = []
for chunk in chunks:
    chunk = chunk.strip()
    if not chunk:
        continue
    soup = BeautifulSoup(chunk, 'html.parser')
    item_cell = soup.select_one('.item-cell')
    if not item_cell:
        continue

    obj = {}

    # Name
    name_el = item_cell.select_one('.item-title')
    obj['Name'] = name_el.get_text(strip=True) if name_el else None

    # URL
    url_el = item_cell.select_one('a.item-title')
    obj['URL'] = url_el['href'] if url_el and url_el.has_attr('href') else None

    # Image
    img_el = item_cell.select_one('.item-img img')
    obj['Image'] = img_el['src'] if img_el and img_el.has_attr('src') else None

    # Price
    price_el = item_cell.select_one('.price-current strong')
    if price_el:
        txt = price_el.get_text(strip=True)
        if txt and txt != 'COMING SOON':
            obj['Price'] = txt.replace(',', '')
        else:
            obj['Price'] = None
    else:
        obj['Price'] = None

    # PriceCents
    cents_el = item_cell.select_one('.price-current sup')
    obj['PriceCents'] = cents_el.get_text(strip=True) if cents_el else None

    # OriginalPrice
    orig_el = item_cell.select_one('.price-was-data')
    obj['OriginalPrice'] = orig_el.get_text(strip=True) if orig_el else None

    # Rating
    rating_el = item_cell.select_one('.item-rating i')
    if rating_el:
        obj['Rating'] = str(rating_el)
    else:
        obj['Rating'] = None

    # ReviewCount
    review_el = item_cell.select_one('.item-rating-num')
    obj['ReviewCount'] = review_el.get_text(strip=True) if review_el else None

    # StockStatus
    stock_el = item_cell.select_one('.item-stock p')
    obj['StockStatus'] = stock_el.get_text(strip=True) if stock_el else None

    # Brand
    brand_el = item_cell.select_one('.item-branding img')
    obj['Brand'] = brand_el['src'] if brand_el and brand_el.has_attr('src') else None

    results.append(obj)

print(json.dumps(results, indent=2))
