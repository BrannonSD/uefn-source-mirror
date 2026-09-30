from PIL import Image, ImageDraw
from pathlib import Path

im = Image.new('RGBA', (256, 256), (0, 0, 0, 0))
d = ImageDraw.Draw(im)
d.rounded_rectangle((8, 8, 248, 248), radius=54, fill='#152234')
d.rounded_rectangle((46, 53, 155, 164), radius=16, outline='#79caff', width=14)
d.rounded_rectangle((101, 97, 210, 208), radius=16, fill='#152234', outline='#62e5ba', width=14)
d.line((122, 129, 108, 151, 122, 174), fill='#62e5ba', width=9)
d.line((187, 129, 201, 151, 187, 174), fill='#62e5ba', width=9)
im.save(Path(__file__).parent / 'src' / 'mirror.ico', sizes=[(16,16),(24,24),(32,32),(48,48),(64,64),(128,128),(256,256)])
