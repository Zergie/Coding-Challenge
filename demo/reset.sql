begin;
select pg_advisory_xact_lock(7493401);
truncate snapshot_components,snapshot_boards,production_snapshots,reservations,
  order_lines,orders,board_recipe,board_revisions,boards,components cascade;

insert into components(id,part_number,name,description,physical_stock) values
  ('11111111-1111-1111-1111-111111111111','RES-10K','10 kΩ resistor','0603 resistor, 10 kΩ',1000),
  ('22222222-2222-2222-2222-222222222222','CAP-100N','100 nF capacitor','0603 decoupling capacitor',600),
  ('33333333-3333-3333-3333-333333333333','LED-GREEN','Green status LED','Unassigned catalog component',150);
insert into boards(id,part_number) values
  ('aaaaaaaa-aaaa-aaaa-aaaa-aaaaaaaaaaaa','CTRL-BOARD');
insert into board_revisions(board_id,revision,name,description,length_mm,width_mm) values
  ('aaaaaaaa-aaaa-aaaa-aaaa-aaaaaaaaaaaa',1,'Controller board','Small SMT controller board',80,50);
insert into board_recipe(board_id,revision,component_id,quantity_per_board) values
  ('aaaaaaaa-aaaa-aaaa-aaaa-aaaaaaaaaaaa',1,'11111111-1111-1111-1111-111111111111',3),
  ('aaaaaaaa-aaaa-aaaa-aaaa-aaaaaaaaaaaa',1,'22222222-2222-2222-2222-222222222222',2);
insert into orders(id,name,description,order_date,due_date,status) values
  ('bbbbbbbb-bbbb-bbbb-bbbb-bbbbbbbbbbbb','Demo build','Ready for first production download',current_date,current_date+7,'Reserved');
insert into order_lines(order_id,board_id,revision,build_quantity) values
  ('bbbbbbbb-bbbb-bbbb-bbbb-bbbbbbbbbbbb','aaaaaaaa-aaaa-aaaa-aaaa-aaaaaaaaaaaa',1,10);
insert into reservations(order_id,component_id,quantity) values
  ('bbbbbbbb-bbbb-bbbb-bbbb-bbbbbbbbbbbb','11111111-1111-1111-1111-111111111111',30),
  ('bbbbbbbb-bbbb-bbbb-bbbb-bbbbbbbbbbbb','22222222-2222-2222-2222-222222222222',20);
commit;
